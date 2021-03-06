// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace Microsoft.Tools.WindowsInstallerXml.Extensions.WixVSExtension
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Execution;
    using Microsoft.Build.Framework;
    using Microsoft.Tools.WindowsInstallerXml.Extensions;

    public class MSBuild15Project : VSProjectHarvester.MSBuildProject
    {
        private BuildManager buildManager;
        private BuildParameters buildParameters;
        private Project currentProject;
        private ProjectInstance currentProjectInstance;
        private ProjectCollection projectCollection;

        public MSBuild15Project(HarvesterCore harvesterCore, string configuration, string platform)
            : base(null, null, null, null)
        {
            this.buildParameters = new BuildParameters();

            try
            {
                HarvestLogger logger = new HarvestLogger();
                logger.HarvesterCore = harvesterCore;
                List<ILogger> loggers = new List<ILogger>();
                loggers.Add(logger);

                this.buildParameters.Loggers = loggers;

                // MSBuild can't handle storing operating environments for nested builds.
                if (Util.RunningInMsBuild)
                {
                    this.buildParameters.SaveOperatingEnvironment = false;
                }
            }
            catch (Exception e)
            {
                if (harvesterCore != null)
                {
                    harvesterCore.OnMessage(VSWarnings.NoLogger(e.Message));
                }
            }

            this.buildManager = new BuildManager();

            if (configuration != null || platform != null)
            {
                Dictionary<string, string> globalVariables = new Dictionary<string, string>();
                if (configuration != null)
                {
                    globalVariables.Add("Configuration", configuration);
                }

                if (platform != null)
                {
                    globalVariables.Add("Platform", platform);
                }

                this.projectCollection = new ProjectCollection(globalVariables);
            }
            else
            {
                this.projectCollection = new ProjectCollection();
            }
        }

        public override bool Build(string projectFileName, string[] targetNames, IDictionary targetOutputs)
        {
            try
            {
                this.buildManager.BeginBuild(this.buildParameters);

                BuildRequestData buildRequestData = new BuildRequestData(this.currentProjectInstance, targetNames, null, BuildRequestDataFlags.ReplaceExistingProjectInstance);

                BuildSubmission submission  = this.buildManager.PendBuildRequest(buildRequestData);

                BuildResult buildResult = submission.Execute();

                bool buildSucceeded = buildResult.OverallResult == BuildResultCode.Success;

                this.buildManager.EndBuild();

                // Fill in empty lists for each target so that heat will look at the item group later.
                foreach (string target in targetNames)
                {
                    targetOutputs.Add(target, new List<object>());
                }

                return buildSucceeded;
            }
            catch (Exception e)
            {
                throw new WixException(VSErrors.CannotBuildProject(projectFileName, e.Message));
            }
        }

        public override VSProjectHarvester.MSBuildProjectItemType GetBuildItem(object buildItem)
        {
            return new MSBuild15ProjectItemType((ProjectItemInstance)buildItem);
        }

        public override IEnumerable GetEvaluatedItemsByName(string itemName)
        {
            return this.currentProjectInstance.GetItems(itemName);
        }

        public override string GetEvaluatedProperty(string propertyName)
        {
            return this.currentProjectInstance.GetPropertyValue(propertyName);
        }

        public override void Load(string projectFileName)
        {
            try
            {
                this.currentProject = this.projectCollection.LoadProject(projectFileName);
                this.currentProjectInstance = this.currentProject.CreateProjectInstance();
            }
            catch (Exception e)
            {
                throw new WixException(VSErrors.CannotLoadProject(projectFileName, e.Message));
            }
        }

        private class MSBuild15ProjectItemType : VSProjectHarvester.MSBuildProjectItemType
        {
            private ProjectItemInstance projectItemInstance;

            public MSBuild15ProjectItemType(ProjectItemInstance projectItemInstance)
                : base(projectItemInstance)
            {
                this.projectItemInstance = projectItemInstance;
            }

            public override string ToString()
            {
                return this.projectItemInstance.EvaluatedInclude;
            }

            public override string GetMetadata(string name)
            {
                return this.projectItemInstance.GetMetadataValue(name);
            }
        }

        // This logger will derive from the Microsoft.Build.Utilities.Logger class,
        // which provides it with getters and setters for Verbosity and Parameters,
        // and a default empty Shutdown() implementation.
        private class HarvestLogger : ILogger
        {
            public HarvesterCore HarvesterCore { get; set; }

            /// <summary>
            /// Initialize is guaranteed to be called by MSBuild at the start of the build
            /// before any events are raised.
            /// </summary>
            public void Initialize(IEventSource eventSource)
            {
                eventSource.ErrorRaised += new BuildErrorEventHandler(eventSource_ErrorRaised);
            }

            public void Shutdown()
            {
            }

            public LoggerVerbosity Verbosity { get;set; }
            public string Parameters { get;set; }

            void eventSource_ErrorRaised(object sender, BuildErrorEventArgs e)
            {
                if (this.HarvesterCore != null)
                {
                    // BuildErrorEventArgs adds LineNumber, ColumnNumber, File, amongst other parameters.
                    string line = String.Format(CultureInfo.InvariantCulture, "{0}({1},{2}): {3}", e.File, e.LineNumber, e.ColumnNumber, e.Message);
                    this.HarvesterCore.OnMessage(VSErrors.BuildErrorDuringHarvesting(line));
                }
            }
        }
    }
}
