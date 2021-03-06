﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Build.Framework.XamlTypes;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.References;
using Microsoft.VisualStudio.ProjectSystem.Utilities;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies
{
    /// <summary>
    /// Provides the special "Dependencies" folder to project trees.
    /// </summary>
    [Export(ExportContractNames.ProjectTreeProviders.PhysicalViewRootGraft, typeof(IProjectTreeProvider))]
    [Export(typeof(IDependenciesGraphProjectContext))]
    [AppliesTo(ProjectCapability.DependenciesTree)]
    internal class DependenciesProjectTreeProvider : ProjectTreeProviderBase, 
                                                     IProjectTreeProvider, 
                                                     IDependenciesGraphProjectContext
    {
        private static readonly ProjectTreeFlags DependenciesRootNodeFlags
                = ProjectTreeFlags.Create(ProjectTreeFlags.Common.BubbleUp
                                          | ProjectTreeFlags.Common.ReferencesFolder
                                          | ProjectTreeFlags.Common.VirtualFolder)
                                  .Add("DependenciesRootNode");

        /// <summary>
        /// Keeps latest updated snapshot of all rules schema catalogs
        /// </summary>
        private IImmutableDictionary<string, IPropertyPagesCatalog> NamedCatalogs { get; set; }

        /// <summary>
        /// Gets the collection of <see cref="IProjectTreePropertiesProvider"/> imports 
        /// that apply to the references tree.
        /// </summary>
        [ImportMany(ReferencesProjectTreeCustomizablePropertyValues.ContractName)]
        private OrderPrecedenceImportCollection<IProjectTreePropertiesProvider> ProjectTreePropertiesProviders { get; set; }

        /// <summary>
        /// Provides a way to extend Dependencies node by consuming sub tree providers that represent
        /// a particular dependency type and are responsible for Dependencies\[TypeNode] and it's contents.
        /// </summary>
        [ImportMany]
        public OrderPrecedenceImportCollection<IProjectDependenciesSubTreeProvider> SubTreeProviders { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DependenciesProjectTreeProvider"/> class.
        /// </summary>
        [ImportingConstructor]
        public DependenciesProjectTreeProvider(IProjectThreadingService threadingService, 
                                               UnconfiguredProject unconfiguredProject)
            : base(threadingService, unconfiguredProject)
        {
            ProjectTreePropertiesProviders = new OrderPrecedenceImportCollection<IProjectTreePropertiesProvider>(
                ImportOrderPrecedenceComparer.PreferenceOrder.PreferredComesLast,
                projectCapabilityCheckProvider: unconfiguredProject);

            SubTreeProviders = new OrderPrecedenceImportCollection<IProjectDependenciesSubTreeProvider>(
                                    ImportOrderPrecedenceComparer.PreferenceOrder.PreferredComesLast,
                                    projectCapabilityCheckProvider: unconfiguredProject);
        }

        /// <summary>
        /// See <see cref="IProjectTreeProvider"/>
        /// </summary>
        /// <remarks>
        /// This stub defined for code contracts.
        /// </remarks>
        IReceivableSourceBlock<IProjectVersionedValue<IProjectTreeSnapshot>> IProjectTreeProvider.Tree
        {
            get { return Tree; }
        }

        /// <summary>
        /// Gets a value indicating whether a given set of nodes can be copied or moved underneath some given node.
        /// </summary>
        /// <param name="nodes">The set of nodes the user wants to copy or move.</param>
        /// <param name="receiver">
        /// The target node where <paramref name="nodes"/> should be copied or moved to.
        /// May be <c>null</c> to determine whether a given set of nodes could allowably be copied anywhere (not 
        /// necessarily everywhere).
        /// </param>
        /// <param name="deleteOriginal"><c>true</c> for a move operation; <c>false</c> for a copy operation.</param>
        /// <returns><c>true</c> if such a move/copy operation would be allowable; <c>false</c> otherwise.</returns>
        public override bool CanCopy(IImmutableSet<IProjectTree> nodes, 
                                     IProjectTree receiver, 
                                     bool deleteOriginal = false)
        {
            return false;
        }

        /// <summary>
        /// Gets a value indicating whether deleting a given set of items from the project, and optionally from disk,
        /// would be allowed. 
        /// Note: CanRemove can be called several times since there two types of remove operations:
        ///   - Remove is a command that can remove project tree items form the tree/project but not from disk. 
        ///     For that command requests deleteOptions has DeleteOptions.None flag.
        ///   - Delete is a command that can remove project tree items and form project and from disk. 
        ///     For this command requests deleteOptions has DeleteOptions.DeleteFromStorage flag.
        /// We can potentially support only Remove command here, since we don't remove Dependencies form disk, 
        /// thus we return false when DeleteOptions.DeleteFromStorage is provided.
        /// </summary>
        /// <param name="nodes">The nodes that should be deleted.</param>
        /// <param name="deleteOptions">
        /// A value indicating whether the items should be deleted from disk as well as from the project file.
        /// </param>
        public override bool CanRemove(IImmutableSet<IProjectTree> nodes, 
                                       DeleteOptions deleteOptions = DeleteOptions.None)
        {
            if (deleteOptions.HasFlag(DeleteOptions.DeleteFromStorage))
            {
                return false;
            }

            return nodes.All(node => (node.Flags.Contains(DependencyNode.GenericDependencyFlags) 
                                        && node.BrowseObjectProperties != null)
                                     || node.Flags.Contains(ProjectTreeFlags.Common.SharedProjectImportReference));
        }

        /// <summary>
        /// Deletes items from the project, and optionally from disk.
        /// Note: Delete and Remove commands are handled via IVsHierarchyDeleteHandler3, not by
        /// IAsyncCommandGroupHandler and first asks us we CanRemove nodes. If yes then RemoveAsync is called.
        /// We can remove only nodes that are standard and based on project items, i.e. nodes that 
        /// are created by default IProjectDependenciesSubTreeProvider implementations and have 
        /// DependencyNode.GenericDependencyFlags flags and IRule with Context != null, in order to obtain 
        /// node's itemSpec. ItemSpec then used to remove a project item having same Include.
        /// </summary>
        /// <param name="nodes">The nodes that should be deleted.</param>
        /// <param name="deleteOptions">A value indicating whether the items should be deleted from disk as well as 
        /// from the project file.
        /// </param>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="IProjectTreeProvider.CanRemove"/> 
        /// would return <c>false</c> for this operation.</exception>
        public override async Task RemoveAsync(IImmutableSet<IProjectTree> nodes, 
                                               DeleteOptions deleteOptions = DeleteOptions.None)
        {
            if (deleteOptions.HasFlag(DeleteOptions.DeleteFromStorage))
            {
                throw new NotSupportedException();
            }

            // Get the list of shared import nodes.
            IEnumerable<IProjectTree> sharedImportNodes = nodes.Where(node => 
                    node.Flags.Contains(ProjectTreeFlags.Common.SharedProjectImportReference));

            // Get the list of normal reference Item Nodes (this excludes any shared import nodes).
            IEnumerable<IProjectTree> referenceItemNodes = nodes.Except(sharedImportNodes);

            using (var access = await ProjectLockService.WriteLockAsync())
            {
                var project = await access.GetProjectAsync(ActiveConfiguredProject).ConfigureAwait(true);

                // Handle the removal of normal reference Item Nodes (this excludes any shared import nodes).
                foreach (var node in referenceItemNodes)
                {
                    if (node.BrowseObjectProperties == null || node.BrowseObjectProperties.Context == null)
                    {
                        // if node does not have an IRule with valid ProjectPropertiesContext we can not 
                        // get it's itemsSpec. If nodes provided by custom IProjectDependenciesSubTreeProvider
                        // implementation, and have some custom IRule without context, it is not a problem,
                        // since they wouldnot have DependencyNode.GenericDependencyFlags and we would not 
                        // end up here, since CanRemove would return false and Remove command would not show 
                        // up for those nodes. 
                        continue;
                    }

                    var nodeItemContext = node.BrowseObjectProperties.Context;
                    var unresolvedReferenceItem = project.GetItemsByEvaluatedInclude(nodeItemContext.ItemName)
                        .FirstOrDefault(item => string.Equals(item.ItemType, 
                                                              nodeItemContext.ItemType, 
                                                              StringComparison.OrdinalIgnoreCase));

                    Report.IfNot(unresolvedReferenceItem != null, "Cannot find reference to remove.");
                    if (unresolvedReferenceItem != null)
                    {
                        await access.CheckoutAsync(unresolvedReferenceItem.Xml.ContainingProject.FullPath)
                                    .ConfigureAwait(true);
                        project.RemoveItem(unresolvedReferenceItem);
                    }
                }

                // Handle the removal of shared import nodes.
                var projectXml = await access.GetProjectXmlAsync(UnconfiguredProject.FullPath)
                                             .ConfigureAwait(true);
                foreach (var sharedImportNode in sharedImportNodes)
                {
                    // Find the import that is included in the evaluation of the specified ConfiguredProject that
                    // imports the project file whose full path matches the specified one.
                    var matchingImports = from import in project.Imports
                                          where import.ImportingElement.ContainingProject == projectXml
                                          where PathHelper.IsSamePath(import.ImportedProject.FullPath, 
                                                                      sharedImportNode.FilePath)
                                          select import;
                    foreach (var importToRemove in matchingImports)
                    {
                        var importingElementToRemove = importToRemove.ImportingElement;
                        Report.IfNot(importingElementToRemove != null, 
                                     "Cannot find shared project reference to remove.");
                        if (importingElementToRemove != null)
                        {
                            await access.CheckoutAsync(importingElementToRemove.ContainingProject.FullPath)
                                        .ConfigureAwait(true);
                            importingElementToRemove.Parent.RemoveChild(importingElementToRemove);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds dependencies child nodes by their path. We need to override it since
        /// we need to find children under either:
        ///     - our dependencies root node.
        ///     - dependency sub tree nodes
        ///     - dependency sub tree top level nodes
        /// (deeper levels will be graph nodes with additional info, not direct dependencies
        /// specified in the project file or project.json)
        /// </summary>
        public override IProjectTree FindByPath(IProjectTree root, string path)
        {
            var dependenciesNode = GetSubTreeRootNode(root, DependenciesRootNodeFlags);
            if (dependenciesNode == null)
            {
                return null;
            }

            // Note: all dependency nodes file path starts with file:/// to make sure we have 
            // valid absolute path everytime.
            if (!path.StartsWith("file:///"))
            {
                // just in case if given path is not in uri format
                path = "file:///" + path.Trim('/');
            }

            var node = dependenciesNode.FindNodeByPath(path);
            if (node == null)
            {
                foreach (var child in dependenciesNode.Children)
                {
                    node = child.FindNodeByPath(path);

                    if (node != null)
                    {
                        break;
                    }
                }
            }

            return node;
        }

        /// <summary>
        /// This is still needed for graph nodes search
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public override string GetPath(IProjectTree node)
        {
            return node.FilePath;
        }

        /// <summary>
        /// Generates the original references directory tree.
        /// </summary>
        protected override void Initialize()
        {
            using (UnconfiguredProjectAsynchronousTasksService.LoadedProject())
            {
                base.Initialize();

                // this.IsApplicable may take a project lock, so we can't do it inline with this method
                // which is holding a private lock.  It turns out that doing it asynchronously isn't a problem anyway,
                // so long as we guard against races with the Dispose method.
                UnconfiguredProjectAsynchronousTasksService.LoadedProjectAsync(
                    async delegate
                    {
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                        UnconfiguredProjectAsynchronousTasksService
                            .UnloadCancellationToken.ThrowIfCancellationRequested();

                        lock (SyncObject)
                        {
                            foreach (var provider in SubTreeProviders)
                            {
                                provider.Value.DependenciesChanged += OnDependenciesChanged;
                            }

                            Verify.NotDisposed(this);
                            var nowait = SubmitTreeUpdateAsync(
                                (treeSnapshot, configuredProjectExports, cancellationToken) =>
                                    {
                                        var dependenciesNode = CreateDependenciesFolder(null);                                        
                                        dependenciesNode = CreateOrUpdateSubTreeProviderNodes(dependenciesNode, 
                                                                                              cancellationToken);

                                        return Task.FromResult(new TreeUpdateResult(dependenciesNode, true));
                                    });                            
                        }

                    },
                    registerFaultHandler: true);
            }
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var provider in SubTreeProviders)
                {
                    provider.Value.DependenciesChanged -= OnDependenciesChanged;
                }

                ProjectContextUnloaded?.Invoke(this, new ProjectContextEventArgs(this));
            }

            base.Dispose(disposing);
        }

        private void OnDependenciesChanged(object sender, DependenciesChangedEventArgs e)
        {
            var nowait = SubmitTreeUpdateAsync(
                (treeSnapshot, configuredProjectExports, cancellationToken) =>
                {
                    var dependenciesNode = treeSnapshot.Value.Tree;
                    dependenciesNode = CreateOrUpdateSubTreeProviderNode(dependenciesNode,
                                                                         e.Provider,
                                                                         changes: e.Changes,
                                                                         cancellationToken: cancellationToken,
                                                                         catalogs: e.Catalogs);

                    ProjectContextChanged?.Invoke(this, new ProjectContextEventArgs(this));

                    // Note: temporary workaround to prevent data sources being out of sync is send null always,
                    // this would stop error dialog, however subscribers could not check for Dependencies tree changes
                    // until real fix is checked it (its fine since there probably should not be any at the moment).
                    return Task.FromResult(new TreeUpdateResult(dependenciesNode, false, null /*e.DataSourceVersions*/));
                });
        }

        /// <summary>
        /// Creates the loading References folder node.
        /// </summary>
        /// <returns>a new "Dependencies" tree node.</returns>
        private IProjectTree CreateDependenciesFolder(IProjectTree oldNode)
        {
            if (oldNode == null)
            {
                var values = new ReferencesProjectTreeCustomizablePropertyValues
                {
                    Caption = VSResources.DependenciesNodeName,
                    Icon = KnownMonikers.Reference.ToProjectSystemType(),
                    ExpandedIcon = KnownMonikers.Reference.ToProjectSystemType(),
                    Flags = DependenciesRootNodeFlags
                };

                ApplyProjectTreePropertiesCustomization(null, values);

                return NewTree(
                         values.Caption,
                         icon: values.Icon,
                         expandedIcon: values.ExpandedIcon,
                         flags: values.Flags);
            }
            else
            {
                return oldNode;
            }
        }

        /// <summary>
        /// Creates or updates nodes for all known IProjectDependenciesSubTreeProvider implementations.
        /// </summary>
        /// <param name="dependenciesNode"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>IProjectTree for root Dependencies node</returns>
        private IProjectTree CreateOrUpdateSubTreeProviderNodes(IProjectTree dependenciesNode,
                                                                CancellationToken cancellationToken)
        {
            Requires.NotNull(dependenciesNode, nameof(dependenciesNode));

            foreach (var subTreeProvider in SubTreeProviders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var providerRootTreeNode = GetSubTreeRootNode(dependenciesNode, 
                                                              subTreeProvider.Value.RootNode.Flags);
                // since this method only creates dependencies providers sub tree nodes
                // at initialization time, changes and catalogs could be null.
                dependenciesNode = CreateOrUpdateSubTreeProviderNode(dependenciesNode,
                                                                     subTreeProvider.Value,
                                                                     changes: null,                                                                    
                                                                     catalogs: null,
                                                                     cancellationToken: cancellationToken);
            }

            return dependenciesNode;
        }

        /// <summary>
        /// Creates or updates a project tree for a given IProjectDependenciesSubTreeProvider
        /// </summary>
        /// <param name="dependenciesNode"></param>
        /// <param name="subTreeProvider"></param>
        /// <param name="changes"></param>
        /// <param name="catalogs">Can be null if sub tree provider does not use design time build</param>
        /// <param name="cancellationToken"></param>
        /// <returns>IProjectTree for root Dependencies node</returns>
        private IProjectTree CreateOrUpdateSubTreeProviderNode(IProjectTree dependenciesNode,
                                                               IProjectDependenciesSubTreeProvider subTreeProvider,
                                                               IDependenciesChangeDiff changes,
                                                               IProjectCatalogSnapshot catalogs,
                                                               CancellationToken cancellationToken)
        {
            Requires.NotNull(dependenciesNode, nameof(dependenciesNode));
            Requires.NotNull(subTreeProvider, nameof(subTreeProvider));
            Requires.NotNull(subTreeProvider.RootNode, nameof(subTreeProvider.RootNode));
            Requires.NotNullOrEmpty(subTreeProvider.RootNode.Caption, nameof(subTreeProvider.RootNode.Caption));
            Requires.NotNullOrEmpty(subTreeProvider.ProviderType, nameof(subTreeProvider.ProviderType));

            var providerRootTreeNode = GetSubTreeRootNode(dependenciesNode,
                                                          subTreeProvider.RootNode.Flags);
            if (subTreeProvider.RootNode.HasChildren || subTreeProvider.ShouldBeVisibleWhenEmpty)
            {
                bool newNode = false;
                if (providerRootTreeNode == null)
                {
                    providerRootTreeNode = NewTree(
                        caption: subTreeProvider.RootNode.Caption,
                        visible: true,
                        filePath: subTreeProvider.RootNode.Id.ToString(),
                        browseObjectProperties: null,
                        flags: subTreeProvider.RootNode.Flags,
                        icon: subTreeProvider.RootNode.Icon.ToProjectSystemType(),
                        expandedIcon: subTreeProvider.RootNode.ExpandedIcon.ToProjectSystemType());

                    newNode = true;
                }

                if (changes != null)
                {
                    foreach (var removedItem in changes.RemovedNodes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return dependenciesNode;
                        }

                        var treeNode = providerRootTreeNode.FindNodeByPath(removedItem.Id.ToString());
                        if (treeNode != null)
                        {
                            providerRootTreeNode = treeNode.Remove();
                        }
                    }

                    foreach (var updatedItem in changes.UpdatedNodes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return dependenciesNode;
                        }

                        var treeNode = providerRootTreeNode.FindNodeByPath(updatedItem.Id.ToString());
                        if (treeNode != null)
                        {

                            var updatedNodeParentContext = GetCustomPropertyContext(treeNode.Parent);
                            var updatedValues = new ReferencesProjectTreeCustomizablePropertyValues
                            {
                                Caption = updatedItem.Caption,
                                Flags = updatedItem.Flags,
                                Icon = updatedItem.Icon.ToProjectSystemType(),
                                ExpandedIcon = updatedItem.ExpandedIcon.ToProjectSystemType()
                            };

                            ApplyProjectTreePropertiesCustomization(updatedNodeParentContext, updatedValues);

                            // update existing tree node properties
                            treeNode = treeNode.SetProperties(
                                caption: updatedItem.Caption,
                                flags: updatedItem.Flags,
                                icon: updatedItem.Icon.ToProjectSystemType(),
                                expandedIcon: updatedItem.ExpandedIcon.ToProjectSystemType());

                            providerRootTreeNode = treeNode.Parent;
                        }
                    }

                    var configuredProjectExports = GetActiveConfiguredProjectExports(ActiveConfiguredProject);
                    foreach (var addedItem in changes.AddedNodes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return dependenciesNode;
                        }
                      
                        var treeNode = providerRootTreeNode.FindNodeByPath(addedItem.Id.ToString());
                        if (treeNode == null)
                        {
                            IRule rule = null;
                            if (addedItem.Properties != null)
                            {
                                // when itemSpec is not in valid absolute path format, property page does not show 
                                // item name correctly.
                                var itemSpec = addedItem.Flags.Contains(DependencyNode.CustomItemSpec)
                                    ? addedItem.Caption
                                    : addedItem.Id.ItemSpec;
                                var itemContext = ProjectPropertiesContext.GetContext(UnconfiguredProject,
                                                                                      addedItem.Id.ItemType,
                                                                                      itemSpec);

                                if (addedItem.Resolved)
                                {
                                    rule = GetRuleForResolvableReference(
                                                itemContext,
                                                new KeyValuePair<string, IImmutableDictionary<string, string>>(
                                                    addedItem.Id.ItemSpec, addedItem.Properties),
                                                catalogs,
                                                configuredProjectExports);
                                }
                                else
                                {
                                    rule = GetRuleForUnresolvableReference(
                                                itemContext,
                                                catalogs,
                                                configuredProjectExports);
                                }
                            }

                            // Notify about tree changes to customization context
                            var customTreePropertyContext = GetCustomPropertyContext(providerRootTreeNode);
                            var customTreePropertyValues = new ReferencesProjectTreeCustomizablePropertyValues
                            {
                                Caption = addedItem.Caption,
                                Flags = addedItem.Flags,
                                Icon = addedItem.Icon.ToProjectSystemType()
                            };

                            ApplyProjectTreePropertiesCustomization(customTreePropertyContext, customTreePropertyValues);

                            treeNode = NewTree(caption: addedItem.Caption,
                                               visible: true,
                                               filePath: addedItem.Id.ToString(),
                                               browseObjectProperties: rule,
                                               flags: addedItem.Flags,
                                               icon: addedItem.Icon.ToProjectSystemType(),
                                               expandedIcon: addedItem.ExpandedIcon.ToProjectSystemType());

                            providerRootTreeNode = providerRootTreeNode.Add(treeNode).Parent;
                        }
                    }
                }

                if (newNode)
                {
                    dependenciesNode = dependenciesNode.Add(providerRootTreeNode).Parent;
                }
                else
                {
                    dependenciesNode = providerRootTreeNode.Parent;
                }
            }
            else
            {
                if (providerRootTreeNode != null)
                {
                    dependenciesNode = dependenciesNode.Remove(providerRootTreeNode);
                }
            }

            return dependenciesNode;
        }

        /// <summary>
        /// Finds the resolved reference item for a given unresolved reference.
        /// </summary>
        /// <param name="allResolvedReferences">The collection of resolved references to search.</param>
        /// <param name="unresolvedItemType">The unresolved reference item type.</param>
        /// <param name="unresolvedItemSpec">The unresolved reference item name.</param>
        /// <returns>The key is item name and the value is the metadata dictionary.</returns>
        private static KeyValuePair<string, IImmutableDictionary<string, string>>? GetResolvedReference(
                        IProjectRuleSnapshot[] allResolvedReferences,
                        string unresolvedItemType,
                        string unresolvedItemSpec)
        {
            Contract.Requires(allResolvedReferences != null);
            Contract.Requires(Contract.ForAll(0, allResolvedReferences.Length, i => allResolvedReferences[i] != null));
            Contract.Requires(!string.IsNullOrEmpty(unresolvedItemType));
            Contract.Requires(!string.IsNullOrEmpty(unresolvedItemSpec));

            foreach (var resolvedReferences in allResolvedReferences)
            {
                foreach (var referencePath in resolvedReferences.Items)
                {
                    string originalItemSpec;
                    if (referencePath.Value.TryGetValue(ResolvedAssemblyReference.OriginalItemSpecProperty,
                                                        out originalItemSpec)
                        && !string.IsNullOrEmpty(originalItemSpec))
                    {
                        if (string.Equals(originalItemSpec, unresolvedItemSpec, StringComparison.OrdinalIgnoreCase))
                        {
                            return referencePath;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Finds an existing tree node that represents a given reference item.
        /// </summary>
        /// <param name="tree">The reference folder to search.</param>
        /// <param name="itemType">The item type of the unresolved reference.</param>
        /// <param name="itemName">The item name of the unresolved reference.</param>
        /// <returns>The matching tree node, or <c>null</c> if none was found.</returns>
        private static IProjectItemTree FindReferenceNode(IProjectTree tree, string itemType, string itemName)
        {
            Contract.Requires(tree != null);
            Contract.Requires(!string.IsNullOrEmpty(itemType));
            Contract.Requires(!string.IsNullOrEmpty(itemName));

            return tree.Children.OfType<IProjectItemTree>()
                       .FirstOrDefault(child => string.Equals(itemType, child.Item.ItemType,
                                                           StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(itemName, child.Item.ItemName,
                                                              StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds a tree node by it's flags. If there many nodes that sattisfy flags, returns first.
        /// </summary>
        /// <param name="parentNode"></param>
        /// <param name="flags"></param>
        /// <returns></returns>
        private static IProjectTree GetSubTreeRootNode(IProjectTree parentNode, ProjectTreeFlags flags)
        {
            foreach (IProjectTree child in parentNode.Children)
            {
                if (child.Flags.Contains(flags))
                {
                    return child;
                }
            }

            return null;
        }

        private IImmutableDictionary<string, IPropertyPagesCatalog> GetNamedCatalogs(IProjectCatalogSnapshot catalogs)
        {
            if (catalogs != null)
            {
                return catalogs.NamedCatalogs;
            }

            if (NamedCatalogs != null)
            {
                return NamedCatalogs;
            }

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                // Note: it is unlikely that we end up here, however for cases when node providers
                // getting their node data not from Design time build events, we might have OnDependenciesChanged
                // event coming before initial design time build event updates NamedCatalogs in this class.
                // Thus, just in case, explicitly request it here (GetCatalogsAsync will accuire a project read lock)
                NamedCatalogs = await ActiveConfiguredProject.Services
                                                             .PropertyPagesCatalog
                                                             .GetCatalogsAsync(CancellationToken.None)
                                                             .ConfigureAwait(false);
            });

            return NamedCatalogs;
        }

        /// <summary>
        /// Gets the rule(s) that applies to a reference.
        /// </summary>
        /// <param name="itemType">The item type on the unresolved reference.</param>
        /// <param name="resolved">
        /// A value indicating whether to return rules for resolved or unresolved reference state.
        /// </param>
        /// <param name="namedCatalogs">The dictionary of catalogs.</param>
        /// <returns>The sequence of matching rules.  Hopefully having exactly one element.</returns>
        private IEnumerable<Rule> GetSchemaForReference(string itemType, 
                                                        bool resolved,
                                                        IImmutableDictionary<string, IPropertyPagesCatalog> namedCatalogs)
        {
            Requires.NotNull(namedCatalogs, nameof(namedCatalogs));

            var browseObjectCatalog = namedCatalogs[PropertyPageContexts.BrowseObject];
            return from schemaName in browseObjectCatalog.GetPropertyPagesSchemas(itemType)
                   let schema = browseObjectCatalog.GetSchema(schemaName)
                   where schema.DataSource != null 
                         && string.Equals(itemType, schema.DataSource.ItemType, StringComparison.OrdinalIgnoreCase)
                         && (resolved == string.Equals(schema.DataSource.SourceType, 
                                                       RuleDataSourceTypes.TargetResults, 
                                                       StringComparison.OrdinalIgnoreCase))
                   select schema;
        }

        /// <summary>
        /// Gets an IRule to attach to a project item so that browse object properties will be displayed.
        /// </summary>
        private IRule GetRuleForResolvableReference(
                        IProjectPropertiesContext unresolvedContext, 
                        KeyValuePair<string, IImmutableDictionary<string, string>> resolvedReference, 
                        IProjectCatalogSnapshot catalogs, 
                        ConfiguredProjectExports configuredProjectExports)
        {
            Requires.NotNull(unresolvedContext, nameof(unresolvedContext));

            var namedCatalogs = GetNamedCatalogs(catalogs);
            var schemas = GetSchemaForReference(unresolvedContext.ItemType, true, namedCatalogs).ToList();
            if (schemas.Count == 1)
            {
                IRule rule = configuredProjectExports.RuleFactory.CreateResolvedReferencePageRule(
                                schemas[0], 
                                unresolvedContext, 
                                resolvedReference.Key, 
                                resolvedReference.Value);
                return rule;
            }
            else
            {
                if (schemas.Count > 1)
                {
                    TraceUtilities.TraceWarning(
                        "Too many rule schemas ({0}) in the BrowseObject context were found.  Only 1 is allowed.", 
                        schemas.Count);
                }

                // Since we have no browse object, we still need to create *something* so that standard property 
                // pages can pop up.
                var emptyRule = RuleExtensions.SynthesizeEmptyRule(unresolvedContext.ItemType);
                return configuredProjectExports.PropertyPagesDataModelProvider.GetRule(
                            emptyRule, 
                            unresolvedContext.File, 
                            unresolvedContext.ItemType, 
                            unresolvedContext.ItemName);
            }
        }

        /// <summary>
        /// Gets an IRule to attach to a project item so that browse object properties will be displayed.
        /// </summary>
        private IRule GetRuleForUnresolvableReference(IProjectPropertiesContext unresolvedContext, 
                                                      IProjectCatalogSnapshot catalogs, 
                                                      ConfiguredProjectExports configuredProjectExports)
        {
            Requires.NotNull(unresolvedContext, nameof(unresolvedContext));
            Requires.NotNull(configuredProjectExports, nameof(configuredProjectExports));

            var namedCatalogs = GetNamedCatalogs(catalogs);
            var schemas = GetSchemaForReference(unresolvedContext.ItemType, false, namedCatalogs).ToList();
            if (schemas.Count == 1)
            {
                Requires.NotNull(namedCatalogs, nameof(namedCatalogs));
                var browseObjectCatalog = namedCatalogs[PropertyPageContexts.BrowseObject];
                return browseObjectCatalog.BindToContext(schemas[0].Name, unresolvedContext);
            }

            if (schemas.Count > 1)
            {
                TraceUtilities.TraceWarning(
                    "Too many rule schemas ({0}) in the BrowseObject context were found. Only 1 is allowed.", 
                    schemas.Count);
            }

            // Since we have no browse object, we still need to create *something* so that standard property 
            // pages can pop up.
            var emptyRule = RuleExtensions.SynthesizeEmptyRule(unresolvedContext.ItemType);
            return configuredProjectExports.PropertyPagesDataModelProvider.GetRule(
                        emptyRule, 
                        unresolvedContext.File, 
                        unresolvedContext.ItemType, 
                        unresolvedContext.ItemName);
        }

        private ProjectTreeCustomizablePropertyContext GetCustomPropertyContext(IProjectTree parent)
        {
            return new ProjectTreeCustomizablePropertyContext
            {
                ExistsOnDisk = false,
                ParentNodeFlags = parent?.Flags ?? default(ProjectTreeFlags)
            };
        }

        private void ApplyProjectTreePropertiesCustomization(
                        IProjectTreeCustomizablePropertyContext context,
                        ReferencesProjectTreeCustomizablePropertyValues values)
        {
            foreach (var provider in ProjectTreePropertiesProviders.ExtensionValues())
            {
                provider.CalculatePropertyValues(context, values);
            }
        }

        /// <summary>
        /// Creates a new instance of the configured project exports class.
        /// </summary>
        protected override ConfiguredProjectExports GetActiveConfiguredProjectExports(
                                ConfiguredProject newActiveConfiguredProject)
        {
            Requires.NotNull(newActiveConfiguredProject, nameof(newActiveConfiguredProject));

            return base.GetActiveConfiguredProjectExports<MyConfiguredProjectExports>(newActiveConfiguredProject);
        }

        #region IDependenciesGraphProjectContext

        /// <summary>
        /// Returns a dependencies node sub tree provider for given dependency provider type.
        /// </summary>
        /// <param name="providerType">
        /// Type of the dependnecy. It is expected to be a unique string associated with a provider. 
        /// </param>
        /// <returns>
        /// Instance of <see cref="IProjectDependenciesSubTreeProvider"/> or null if there no provider 
        /// for given type.
        /// </returns>
        IProjectDependenciesSubTreeProvider IDependenciesGraphProjectContext.GetProvider(string providerType)
        {
            if (string.IsNullOrEmpty(providerType))
            {
                return null;
            }

            var lazyProvider = SubTreeProviders.FirstOrDefault(x => providerType.Equals(x.Value.ProviderType,
                                                                            StringComparison.OrdinalIgnoreCase));
            if (lazyProvider == null)
            {
                return null;
            }

            return lazyProvider.Value;
        }

        IEnumerable<IProjectDependenciesSubTreeProvider> IDependenciesGraphProjectContext.GetProviders()
        {
            return SubTreeProviders.Select(x => x.Value).ToList();
        }

        /// <summary>
        /// Path to project file
        /// </summary>
        string IDependenciesGraphProjectContext.ProjectFilePath
        {
            get
            {
                return UnconfiguredProject.FullPath;
            }
        }

        /// <summary>
        /// Gets called when dependencies change
        /// </summary>
        public event EventHandler<ProjectContextEventArgs> ProjectContextChanged;

        /// <summary>
        /// Gets called when project is unloading and dependencies subtree is disposing
        /// </summary>
        public event EventHandler<ProjectContextEventArgs> ProjectContextUnloaded;

        #endregion

        /// <summary>
        /// Describes services collected from the active configured project.
        /// </summary>
        [Export]
        protected class MyConfiguredProjectExports : ConfiguredProjectExports
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="MyConfiguredProjectExports"/> class.
            /// </summary>
            [ImportingConstructor]
            protected MyConfiguredProjectExports(ConfiguredProject configuredProject)
                : base(configuredProject)
            {
            }
        }

        /// <summary>
        /// A private implementation of <see cref="IProjectTreeCustomizablePropertyContext"/>.
        /// </summary>
        private class ProjectTreeCustomizablePropertyContext : IProjectTreeCustomizablePropertyContext
        {
            public string ItemName { get; set; }

            public string ItemType { get; set; }

            public IImmutableDictionary<string, string> Metadata { get; set; }

            public ProjectTreeFlags ParentNodeFlags { get; set; }

            public bool ExistsOnDisk { get; set; }

            public bool IsFolder
            {
                get
                {
                    return false;
                }
            }

            public bool IsNonFileSystemProjectItem
            {
                get
                {
                    return true;
                }
            }

            public IImmutableDictionary<string, string> ProjectTreeSettings { get; set; }
        }
    }
}
