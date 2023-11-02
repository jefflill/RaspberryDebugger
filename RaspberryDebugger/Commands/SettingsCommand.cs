﻿//-----------------------------------------------------------------------------
// FILE:	    SettingsCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2023 by neonFORGE, LLC.  All rights reserved.
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

using System;
using System.ComponentModel.Design;
using System.Diagnostics.Contracts;
using System.Windows.Forms;
using System.Threading.Tasks;

using EnvDTE80;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using Neon.Common;

using RaspberryDebugger.Dialogs;

namespace RaspberryDebugger.Commands
{
    /// <summary>
    /// Handles the <b>Project/Raspberry Debug Settings...</b> command.
    /// </summary>
    internal sealed class SettingsCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        private const int CommandId = RaspberryDebuggerPackage.SettingsCommandId;

        /// <summary>
        /// Package command set ID.
        /// </summary>
        private static readonly Guid CommandSet = RaspberryDebuggerPackage.CommandSet;

        private readonly DTE2 dte;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private SettingsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            Covenant.Requires<ArgumentNullException>(package != null, nameof(package));
            Covenant.Requires<ArgumentNullException>(commandService != null, nameof(commandService));
            
            ThreadHelper.ThrowIfNotOnUIThread();

            this.dte     = (DTE2)Package.GetGlobalService(typeof(SDTE));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem      = new OleMenuCommand(this.Execute, menuCommandId);

            menuItem.BeforeQueryStatus +=
                // ReSharper disable once UnusedParameter.Local
                (s, a) =>
                {
                    var command = (OleMenuCommand)s;
                        
                    command.Visible = PackageHelper.IsActiveProjectRaspberryCompatible(dte);
                };
             
            commandService?.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        // ReSharper disable once UnusedAutoPropertyAccessor.Local
        private static SettingsCommand Instance { get; set; }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

            SettingsCommand.Instance = new SettingsCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
#pragma warning disable VSTHRD100
        private async void Execute(object sender, EventArgs e)
#pragma warning restore VSTHRD100 
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (dte.Solution == null)
            {
                return;
            }

            var project = PackageHelper.GetStartupProject(dte.Solution);

            if (project == null)
            {
                MessageBoxEx.Show(
                    "Please select a startup project using the Project/Set as " +
                    "Startup project menu or by right clicking a project in " +
                    "the Solution Explorer and enabling this.",
                    "Startup Project not found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return;
            }

            var targetFrameworkMonikers = (string)project.Properties.Item("TargetFrameworkMoniker").Value;
            var outputType              = (int)project.Properties.Item("OutputType").Value;
            var monikers                = targetFrameworkMonikers.Split(',');
            var isNetCore               = monikers[0] == ".NETCoreApp";
            var sdkVersion              = monikers[1].StartsWith("Version=v") ? monikers[1].Substring("Version=v".Length) : null;

            if (!isNetCore ||
                outputType != 1 /* EXE */ ||
                sdkVersion == null ||
                SemanticVersion.Parse(sdkVersion) < SemanticVersion.Parse("3.1"))
            {
                MessageBoxEx.Show(
                    "Raspberry debugging is not supported by this project type.  Only .NET Core applications targeting .NET Core 3.1 or greater are supported.",
                    "Unsupported Project Type",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var raspberryProjects = PackageHelper.ReadRaspberryProjects(dte.Solution);
            var projectSettings   = raspberryProjects[project.UniqueName];
            var settingsDialog    = new SettingsDialog(projectSettings);

            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                PackageHelper.WriteRaspberryProjects(dte.Solution, raspberryProjects);
            }
        }
    }
}
