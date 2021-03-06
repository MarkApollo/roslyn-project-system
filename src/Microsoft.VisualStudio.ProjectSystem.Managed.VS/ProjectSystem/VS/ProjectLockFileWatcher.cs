﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using TPL = System.Threading.Tasks;

namespace Microsoft.VisualStudio.ProjectSystem.VS
{
    internal class ProjectLockFileWatcher : OnceInitializedOnceDisposed, IVsFileChangeEvents
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IUnconfiguredProjectCommonServices _projectServices;
        private readonly IProjectLockService _projectLockService;
        private readonly IProjectTreeProvider _fileSystemTreeProvider;
        private readonly IVsFileChangeEx _fileChangeService;
        private IDisposable _treeWatcher;
        private uint _filechangeCookie;
        private string _fileBeingWatched;

        [ImportingConstructor]
        public ProjectLockFileWatcher([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider,
                                      [Import(ContractNames.ProjectTreeProviders.FileSystemDirectoryTree)] IProjectTreeProvider fileSystemTreeProvider,
                                      IUnconfiguredProjectCommonServices projectServices,
                                      IProjectLockService projectLockService)
        {
            Requires.NotNull(serviceProvider, nameof(serviceProvider));
            Requires.NotNull(fileSystemTreeProvider, nameof(fileSystemTreeProvider));
            Requires.NotNull(projectServices, nameof(projectServices));
            Requires.NotNull(projectLockService, nameof(projectLockService));

            _serviceProvider = serviceProvider;
            _fileSystemTreeProvider = fileSystemTreeProvider;
            _projectServices = projectServices;
            _projectLockService = projectLockService;
            _fileChangeService = _serviceProvider.GetService<IVsFileChangeEx, SVsFileChangeEx>();
        }

        /// <summary>
        /// Called on project load.
        /// </summary>
        [ConfiguredProjectAutoLoad]
        [AppliesTo(ProjectCapability.CSharpOrVisualBasic)]
        internal void Load()
        {
            this.EnsureInitialized();
        }
        
        /// <summary>
        /// Initialize the watcher.
        /// </summary>
        protected override void Initialize()
        {
            _treeWatcher = _fileSystemTreeProvider.Tree.LinkTo(new ActionBlock<IProjectVersionedValue<IProjectTreeSnapshot>>(new Action<IProjectVersionedValue<IProjectTreeSnapshot>>(this.ProjectTree_ChangedAsync)));
        }

        /// <summary>
        /// Called on changes to the project tree.
        /// </summary>
        internal async void ProjectTree_ChangedAsync(IProjectVersionedValue<IProjectTreeSnapshot> treeSnapshot)
        {
            var newTree = treeSnapshot.Value.Tree;
            if (newTree == null)
            {
                return;
            }

            // If tree changed when we are disposing then ignore the change.
            if (this.IsDisposing)
            {
                return;
            }

            var projectLockFilePath = await GetProjectLockFilePathAsync(newTree).ConfigureAwait(false);

            // project.json may have been renamed to {projectName}.project.json. In that case change the file watcher.
            if (!PathHelper.IsSamePath(projectLockFilePath, _fileBeingWatched))
            {
                UnregisterFileWatcherIfAny();
                RegisterFileWatcherAsync(projectLockFilePath);
                _fileBeingWatched = projectLockFilePath;
            }
        }

        private async TPL.Task<String> GetProjectLockFilePathAsync(IProjectTree newTree)
        {
            // First check to see if the project has a project.json. 
            IProjectTree projectJsonNode = FindProjectJsonNode(newTree);
            if (projectJsonNode != null)
            {
                var projectDirectory = Path.GetDirectoryName(_projectServices.Project.FullPath);
                var projectLockJsonFilePath = Path.ChangeExtension(PathHelper.Combine(projectDirectory, projectJsonNode.Caption), ".lock.json");
                return projectLockJsonFilePath;
            }

            // If there is no project.json then get the patch to obj\project.assets.json file which is generated for projects
            // with <PackageReference> items.
            var configurationGeneral = await _projectServices.ActiveConfiguredProjectProperties.GetConfigurationGeneralPropertiesAsync().ConfigureAwait(false);
            var objDirectory = (string) await configurationGeneral.BaseIntermediateOutputPath.GetValueAsync().ConfigureAwait(false);
            objDirectory = _projectServices.Project.MakeRooted(objDirectory);
            var projectAssetsFilePath = PathHelper.Combine(objDirectory, "project.assets.json");
            return projectAssetsFilePath;
        }

        private IProjectTree FindProjectJsonNode(IProjectTree newTree)
        {
            IProjectTree projectJsonNode;
            if (newTree.TryFindImmediateChild("project.json", out projectJsonNode))
            {
                return projectJsonNode;
            }

            var projectName = Path.GetFileNameWithoutExtension(_projectServices.Project.FullPath);
            if (newTree.TryFindImmediateChild($"{projectName}.project.json", out projectJsonNode))
            {
                return projectJsonNode;
            }

            return null;
        }
        
        private void RegisterFileWatcherAsync(string projectLockJsonFilePath)
        {
            if (_fileChangeService != null)
            {
                int hr = _fileChangeService.AdviseFileChange(projectLockJsonFilePath, (uint)(_VSFILECHANGEFLAGS.VSFILECHG_Time | _VSFILECHANGEFLAGS.VSFILECHG_Size | _VSFILECHANGEFLAGS.VSFILECHG_Add | _VSFILECHANGEFLAGS.VSFILECHG_Del), this, out _filechangeCookie);
                ErrorHandler.ThrowOnFailure(hr);
            }
        }

        private void UnregisterFileWatcherIfAny()
        {
            if (_filechangeCookie != VSConstants.VSCOOKIE_NIL && _fileChangeService != null)
            {
                // There's nothing for us to do if this fails. So ignore the return value.
                _fileChangeService?.UnadviseFileChange(_filechangeCookie);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _treeWatcher.Dispose();
                UnregisterFileWatcherIfAny();
            }
        }

        /// <summary>
        /// Called when a project.lock.json file changes.
        /// </summary>
        public int FilesChanged(uint cChanges, string[] rgpszFile, uint[] rggrfChange)
        {
            // Kick off the operation to notify the project change in a different thread irregardless of
            // the kind of change since we are interested in all changes.
            _projectServices.ThreadingService.Fork(async () => { 
                using (var access = await _projectLockService.WriteLockAsync())
                {
                    // Inside a write lock, we should get back to the same thread.
                    var project = await access.GetProjectAsync(_projectServices.ActiveConfiguredProject).ConfigureAwait(true);
                    project.MarkDirty();
                    _projectServices.ActiveConfiguredProject.NotifyProjectChange();
                }
            }, configuredProject: _projectServices.ActiveConfiguredProject);

            return VSConstants.S_OK;
        }

        public int DirectoryChanged(string pszDirectory)
        {
            return VSConstants.S_OK;
        }
    }
}
