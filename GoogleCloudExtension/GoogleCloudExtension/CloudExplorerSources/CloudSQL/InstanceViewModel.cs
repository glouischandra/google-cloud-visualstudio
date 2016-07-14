﻿// Copyright 2016 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Apis.SQLAdmin.v1beta4.Data;
using GoogleCloudExtension.Analytics;
using GoogleCloudExtension.CloudExplorer;
using GoogleCloudExtension.DataSources;
using GoogleCloudExtension.MySQLInstaller;
using GoogleCloudExtension.Utils;
using Microsoft.VisualStudio.Data;
using Microsoft.VisualStudio.Shell;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;

namespace GoogleCloudExtension.CloudExplorerSources.CloudSQL
{
    /// <summary>
    /// This class represents a view of a Cloud SQL instance (MySQL instance) in the Properties Window.
    /// </summary>
    internal class InstanceViewModel : TreeHierarchy, ICloudExplorerItemSource
    {
        private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(1);

        private const string IconRunningResourcePath = "CloudExplorerSources/CloudSQL/Resources/instance_icon_running.png";
        private const string IconOfflineResourcePath = "CloudExplorerSources/CloudSQL/Resources/instance_icon_offline.png";
        private const string IconUnknownResourcePath = "CloudExplorerSources/CloudSQL/Resources/instance_icon_unknown.png";

        private static readonly Lazy<ImageSource> s_instanceRunningIcon = new Lazy<ImageSource>(() => ResourceUtils.LoadImage(IconRunningResourcePath));
        private static readonly Lazy<ImageSource> s_instanceOfflineIcon = new Lazy<ImageSource>(() => ResourceUtils.LoadImage(IconOfflineResourcePath));
        private static readonly Lazy<ImageSource> s_instanceUnknownIcon = new Lazy<ImageSource>(() => ResourceUtils.LoadImage(IconUnknownResourcePath));

        private readonly CloudSQLSourceRootViewModel _owner;

        private DatabaseInstance _instance;

        private DatabaseInstance Instance
        {
            get { return _instance; }
            set
            {
                _instance = value;
                UpdateIcon();
                ItemChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ItemChanged;

        public object Item => GetItem();

        public InstanceViewModel(CloudSQLSourceRootViewModel owner, DatabaseInstance instance)
        {
            _owner = owner;
            _instance = instance;

            Caption = Instance.Name;

            UpdateMenu();
            UpdateIcon();
        }

        /// <summary>
        /// Add the current machine's IP address as an authorized network of the database instance and
        /// shows status for the operation while it is in progress.
        /// </summary>
        private void AuthorizeMachine()
        {
            ExtensionAnalytics.ReportCommand(CommandName.GrantMachineDatabaseAccess, CommandInvocationSource.ContextMenu);
            DatabaseInstanceExtensions.AddAuthorizedNetwork(Instance, DnsUtils.MachineIpAddress);
            Task<Operation> operation = _owner.DataSource.Value.UpdateInstanceAsync(Instance);
            string action = Resources.CloudExplorerSqlAthorizeMachineCaption;
            string errorMessage = Resources.CloudExplorerSqlAthorizeMachineErrorMessage;
            PollOperation(operation, action, errorMessage);
        }

        /// <summary>
        /// Remove the current machine's IP address as an authorized network of the database instance and
        /// shows status for the operation while it is in progress.
        /// </summary>
        private void UnauthorizeMachine()
        {
            ExtensionAnalytics.ReportCommand(CommandName.RevokeMachineDatabaseAccess, CommandInvocationSource.ContextMenu);
            DatabaseInstanceExtensions.RemoveAuthorizedNetwork(Instance, DnsUtils.MachineIpAddress);
            Task<Operation> operation = _owner.DataSource.Value.UpdateInstanceAsync(Instance);
            string action = Resources.CloudExplorerSqlUnathorizeMachineCaption;
            string errorMessage = Resources.CloudExplorerSqlUnathorizeMachinerrorMessage;
            PollOperation(operation, action, errorMessage);
        }

        /// <summary>
        /// Poll the status of a pending operation until it is complete.
        /// </summary>
        /// <param name="task">The current operation to watch and poll</param>
        /// <param name="action">A string representation of what the operation is doing for user display</param>
        /// <param name="errorMessage">A user friendly error message to show to users if an failure occurs</param>
        private async void PollOperation(Task<Operation> task, string action, string errorMessage)
        {
            UserPromptUtils.ErrorPrompt(
                    "message", "title");
            // Update the user display and menu.
            IsLoading = true;
            UpdateMenu();
            Caption = action;
            CloudSqlDataSource dataSource = _owner.DataSource.Value;

            try
            {
                // Poll until the update to completes.
                Polling<Operation>.Fetch fetch = (o) => dataSource.GetOperationAsync(o.Name);
                Polling<Operation>.StopPolling stopPolling =
                    (o) => CloudSqlDataSource.OperationStateDone.Equals(o.Status);
                Operation operation = await Polling<Operation>.Poll(task, fetch, stopPolling);

                // Be sure to update the instance when finished to ensure we have
                // the most up to date version.
                Instance = await dataSource.GetInstanceAsync(Instance.Name);
            }
            catch  (DataSourceException ex)
            {
                IsError = true;
                UserPromptUtils.ErrorPrompt(ex.Message, errorMessage);
            }
            catch (TimeoutException ex)
            {
                IsError = true;
                UserPromptUtils.ErrorPrompt(Resources.CloudExploreOperationTimeoutMessage, errorMessage);
            }
            finally
            {
                // Update the user display and menu.
                IsLoading = false;
                UpdateMenu();
                Caption = Instance.Name;
            }
        }

        private void OnOpenOnCloudConsoleCommand()
        {
            var url = $"https://console.cloud.google.com/sql/instances/{_instance.Name}/overview?project={_owner.Context.CurrentProject.Name}";
            Process.Start(url);
        }

        private void OnPropertiesCommand()
        {
            _owner.Context.ShowPropertiesWindow(Item);
        }

        /// <summary>
        /// Opens the Add Data Connection Dialog with the data source being a MySQL database and the server field
        /// set to the ip of the intance.  If the proper dependencies are not installed (for the MySQL database)
        /// the user will be prompted to install them before they can continue.
        /// </summary>
        private void OpenDataConnectionDialog()
        {
            ExtensionAnalytics.ReportCommand(CommandName.OpenMySQLDataConnectionDialog, CommandInvocationSource.Button);

            // Create a data connection dialog and add all possible data sources to it.
            DataConnectionDialogFactory factory = (DataConnectionDialogFactory)Package.GetGlobalService(typeof(DataConnectionDialogFactory));
            DataConnectionDialog dialog = factory.CreateConnectionDialog();
            dialog.AddAllSources();

            // Check if the MySQL data source exists.
            // TODO(talarico): This is added when the user has MySQL for Visual Studio installed.  We should also
            // probably check for the needed pieces in the MySQL Connector/Net.
            if (dialog.AvailableSources.Contains(MySQLUtils.MySQLDataSource))
            {
                // Pre select the MySQL data source.
                dialog.SelectedSource = MySQLUtils.MySQLDataSource;

                // Create the connection string to pre populate the server address in the dialog.
                MySqlConnectionStringBuilder builderPrePopulate = new MySqlConnectionStringBuilder();
                InstanceItem instance = GetItem();
                builderPrePopulate.Server = String.IsNullOrEmpty(instance.IpAddress) ? instance.Ipv6Address : instance.IpAddress;
                dialog.DisplayConnectionString = builderPrePopulate.GetConnectionString(false);

                bool addDataConnection = dialog.ShowDialog();
                if (addDataConnection)
                {
                    ExtensionAnalytics.ReportCommand(CommandName.AddMySQLDataConnection, CommandInvocationSource.Button);

                    // Create a name for the data connection
                    MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder(dialog.DisplayConnectionString);
                    string database = $"{Instance.Project}[{builder.Server}][{builder.Database}]";

                    // Add the MySQL data connection to the data explorer
                    DataExplorerConnectionManager manager = (DataExplorerConnectionManager)Package.GetGlobalService(typeof(DataExplorerConnectionManager));
                    manager.AddConnection(database, MySQLUtils.MySQLDataProvider, dialog.EncryptedConnectionString, true);
                }
            }
            else
            {
                // MySQL for Visual Studio isn't installed, prompt the user to install it.
                ExtensionAnalytics.ReportEvent("MySQLForVisualStudio", "Missing");
                MySQLInstallerWindow.PromptUser();
            }
        }

        /// <summary>
        /// Update the context menu based on the current state of the instance.
        /// </summary>
        private void UpdateMenu()
        {
            // Do not allow actions when the instance is loading or in an error state.
            if (IsLoading || IsError)
            {
                ContextMenu = null;
                return;
            }

            var menuItems = new List<MenuItem>
            {
                new MenuItem { Header = Resources.CloudExplorerSqlOpenAddDataConnectionMenuHeader, Command = new WeakCommand(OpenDataConnectionDialog) },
                new MenuItem { Header = Resources.UiOpenOnCloudConsoleMenuHeader, Command = new WeakCommand(OnOpenOnCloudConsoleCommand) },
                new MenuItem { Header = Resources.UiPropertiesMenuHeader, Command = new WeakCommand(OnPropertiesCommand) },
            };


            // If the machine address could be found allow the user to grant and remove access for the current machine
            if (DnsUtils.MachineIpAddress != null)
            {
                if (DatabaseInstanceExtensions.IpAddressAuthorized(Instance, DnsUtils.MachineIpAddress))
                {
                    menuItems.Add(new MenuItem {Header = Resources.CloudExplorerSqlUnathorizeMachineMenuHeader, Command = new WeakCommand(UnauthorizeMachine) });
                }
                else
                {
                    menuItems.Add(new MenuItem {Header = Resources.CloudExplorerSqlAthorizeMachineMenuHeader, Command = new WeakCommand(AuthorizeMachine) });
                }
            }

            ContextMenu = new ContextMenu { ItemsSource = menuItems };
        }

        /// <summary>
        /// Update the icon menu based on the current state of the instance.
        /// </summary>
        private void UpdateIcon()
        {
            switch (Instance.State)
            {
                case DatabaseInstanceExtensions.RunnableState:
                case DatabaseInstanceExtensions.PendingCreateState:
                    Icon = s_instanceRunningIcon.Value;
                    break;

                case DatabaseInstanceExtensions.SuspendedState:
                case DatabaseInstanceExtensions.MaintenanceState:
                    Icon = s_instanceOfflineIcon.Value;
                    break;

                default:
                    Icon = s_instanceUnknownIcon.Value;
                    break;
            }
        }

        public InstanceItem GetItem() => new InstanceItem(Instance);
    }
}
