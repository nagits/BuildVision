﻿using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BuildVision.Common;
using BuildVision.Contracts;
using BuildVision.Contracts.Models;
using BuildVision.Core;
using BuildVision.Exports.Providers;
using BuildVision.Exports.Services;
using BuildVision.Exports.ViewModels;
using BuildVision.Helpers;
using BuildVision.UI.Common.Logging;
using BuildVision.UI.DataGrid;
using BuildVision.UI.Helpers;
using BuildVision.UI.Models;
using BuildVision.UI.Settings.Models;
using BuildVision.Views.Settings;
using Microsoft.VisualStudio;
using Process = System.Diagnostics.Process;
using SortDescription = BuildVision.UI.Settings.Models.Sorting.SortDescription;

namespace BuildVision.UI.ViewModels
{
    [Export(typeof(IBuildVisionPaneViewModel))]
    public class BuildVisionPaneViewModel : BindableBase, IBuildVisionPaneViewModel
    {
        private ProjectItem _selectedProjectItem;
        private readonly IBuildInformationProvider _buildInformationProvider;
        private readonly IBuildService _buildService;
        private readonly IErrorNavigationService _errorNavigationService;
        private readonly ITaskBarInfoService _taskBarInfoService;
        private readonly IPackageSettingsProvider _settingsProvider;
        private ObservableCollection<DataGridColumn> _gridColumnsRef;

        public ISolutionModel SolutionModel { get; set; }

        public string GridGroupHeaderName => string.IsNullOrEmpty(GridGroupPropertyName) ? string.Empty : ControlSettings.GridSettings.Columns[GridGroupPropertyName].Header;

        public CompositeCollection GridColumnsGroupMenuItems => CreateContextMenu();

        public bool HideUpToDateTargets
        {
            get => ControlSettings.GeneralSettings.HideUpToDateTargets;
            set => SetProperty(() => ControlSettings.GeneralSettings.HideUpToDateTargets, val => ControlSettings.GeneralSettings.HideUpToDateTargets = val, value);
        }

        public ControlSettings ControlSettings { get; }

        public ObservableCollection<IProjectItem> Projects { get; set; }

        public IBuildInformationModel BuildInformationModel { get; set; }

        public string GridGroupPropertyName
        {
            get { return ControlSettings.GridSettings.GroupName; }
            set
            {
                if (ControlSettings.GridSettings.GroupName != value)
                {
                    ControlSettings.GridSettings.GroupName = value;
                    OnPropertyChanged(nameof(GridGroupPropertyName));
                    OnPropertyChanged(nameof(GroupedProjectsList));
                    OnPropertyChanged(nameof(GridColumnsGroupMenuItems));
                    OnPropertyChanged(nameof(GridGroupHeaderName));
                }
            }
        }

        private CompositeCollection CreateContextMenu()
        {
            var collection = new CompositeCollection
            {
                new MenuItem
                {
                    Header = Resources.NoneMenuItem,
                    Tag = string.Empty
                }
            };

            foreach (var column in ControlSettings.GridSettings.Columns.Where(ColumnsManager.ColumnIsGroupable))
            {
                string header = column.Header;
                var menuItem = new MenuItem
                {
                    Header = !string.IsNullOrEmpty(header)
                                ? header
                                : ColumnsManager.GetInitialColumnHeader(column),
                    Tag = column.PropertyNameId
                };

                collection.Add(menuItem);
            }

            foreach (MenuItem menuItem in collection)
            {
                menuItem.IsCheckable = false;
                menuItem.StaysOpenOnClick = false;
                menuItem.IsChecked = (GridGroupPropertyName == (string) menuItem.Tag);
                menuItem.Command = GridGroupPropertyMenuItemClicked;
                menuItem.CommandParameter = menuItem.Tag;
            }

            return collection;
        }

        public SortDescription GridSortDescription
        {
            get => ControlSettings.GridSettings.Sort;
            set
            {
                if (ControlSettings.GridSettings.Sort != value)
                {
                    ControlSettings.GridSettings.Sort = value;
                    OnPropertyChanged(nameof(GridSortDescription));
                    OnPropertyChanged(nameof(GroupedProjectsList));
                }
            }
        }

        // Should be initialized by View.
        public void SetGridColumnsRef(ObservableCollection<DataGridColumn> gridColumnsRef)
        {
            if (_gridColumnsRef != gridColumnsRef)
            {
                _gridColumnsRef = gridColumnsRef;
                GenerateColumns();
            }
        }

        // TODO: Rewrite using CollectionViewSource? 
        // http://stackoverflow.com/questions/11505283/re-sort-wpf-datagrid-after-bounded-data-has-changed
        public ListCollectionView GroupedProjectsList
        {
            get
            {
                var groupedList = new ListCollectionView(Projects); // todo use projects here ProjectsList);

                if (!string.IsNullOrWhiteSpace(GridGroupPropertyName))
                {
                    Debug.Assert(groupedList.GroupDescriptions != null);
                    groupedList.GroupDescriptions.Add(new PropertyGroupDescription(GridGroupPropertyName));
                }

                groupedList.CustomSort = GetProjectItemSorter(GridSortDescription);
                groupedList.IsLiveGrouping  = true;
                groupedList.IsLiveSorting = true;
                return groupedList;
            }
        }

        public DataGridHeadersVisibility GridHeadersVisibility
        {
            get
            {
                return ControlSettings.GridSettings.ShowColumnsHeader
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            }
            set
            {
                bool showColumnsHeader = (value != DataGridHeadersVisibility.None);
                if (ControlSettings.GridSettings.ShowColumnsHeader != showColumnsHeader)
                {
                    ControlSettings.GridSettings.ShowColumnsHeader = showColumnsHeader;
                    OnPropertyChanged(nameof(GridHeadersVisibility));
                }
            }
        }

        public ProjectItem SelectedProjectItem
        {
            get => _selectedProjectItem; 
            set => SetProperty(ref _selectedProjectItem, value);
        }

        internal BuildVisionPaneViewModel()
        {
            ControlSettings = new ControlSettings();
            BuildInformationModel = new BuildInformationModel();
            SolutionModel = new SolutionModel();
            Projects = new ObservableCollection<IProjectItem>();
        }

        [ImportingConstructor]
        public BuildVisionPaneViewModel(
            IBuildInformationProvider buildInformationProvider, 
            IPackageSettingsProvider settingsProvider, 
            ISolutionProvider solutionProvider, 
            IBuildService buildService,
            IErrorNavigationService errorNavigationService,
            ITaskBarInfoService taskBarInfoService)
        {
            _buildInformationProvider = buildInformationProvider;
            _buildService = buildService;
            _errorNavigationService = errorNavigationService;
            _taskBarInfoService = taskBarInfoService;
            BuildInformationModel = _buildInformationProvider.BuildInformationModel;
            SolutionModel = solutionProvider.GetSolutionModel();
            ControlSettings = settingsProvider.Settings;
            Projects = _buildInformationProvider.Projects;

            _settingsProvider = settingsProvider;
            _settingsProvider.SettingsChanged += () =>
            {
                SyncColumnSettings();
                OnControlSettingsChanged();
            };

            if (settingsProvider.Settings.GeneralSettings.FillProjectListOnBuildBegin)
            {
                Projects.CollectionChanged += (sender, e) =>
                {
                    OnPropertyChanged(nameof(GroupedProjectsList));
                };
            }
        }

        private void OpenContainingFolder()
        {
            try
            {
                string dir = Path.GetDirectoryName(SelectedProjectItem.FullName);
                Debug.Assert(dir != null);
                Process.Start(dir);
            }
            catch (Exception ex)
            {                
                ex.Trace(string.Format(
                    "Unable to open folder '{0}' containing the project '{1}'.",
                    SelectedProjectItem.FullName,
                    SelectedProjectItem.UniqueName));

                MessageBox.Show(
                    ex.Message + "\n\nSee log for details.",
                    Resources.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ReorderGrid(object obj)
        {
            var e = (DataGridSortingEventArgs)obj;

            ListSortDirection? oldSortDirection = e.Column.SortDirection;
            ListSortDirection? newSortDirection;
            switch (oldSortDirection)
            {
                case null:
                    newSortDirection = ListSortDirection.Ascending;
                    break;
                case ListSortDirection.Ascending:
                    newSortDirection = ListSortDirection.Descending;
                    break;
                case ListSortDirection.Descending:
                    newSortDirection = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(obj));
            }

            e.Handled = true;
            e.Column.SortDirection = newSortDirection;

            GridSortDescription = new SortDescription(newSortDirection.ToMedia(), e.Column.GetBindedProperty());
        }

        private static ProjectItemColumnSorter GetProjectItemSorter(SortDescription sortDescription)
        {
            var sortOrder = sortDescription.Order;
            string sortPropertyName = sortDescription.Property;

            if (sortOrder != SortOrder.None && !string.IsNullOrEmpty(sortPropertyName))
            {
                ListSortDirection? sortDirection = sortOrder.ToSystem();
                Debug.Assert(sortDirection != null);

                try
                {
                    return new ProjectItemColumnSorter(sortDirection.Value, sortPropertyName);
                }
                catch (PropertyNotFoundException ex)
                {
                    ex.Trace("Trying to sort Project Items by nonexistent property.");
                    return null;
                }
            }

            return null;
        }

        public void GenerateColumns()
        {
            Debug.Assert(_gridColumnsRef != null);
            ColumnsManager.GenerateColumns(_gridColumnsRef, ControlSettings.GridSettings);
        }

        public void SyncColumnSettings()
        {
            Debug.Assert(_gridColumnsRef != null);
            ColumnsManager.SyncColumnSettings(_gridColumnsRef, ControlSettings.GridSettings);
        }

        public void OnControlSettingsChanged()
        {
            ControlSettings.InitFrom(_settingsProvider.Settings);
            GenerateColumns();
            // Raise all properties have changed.
            OnPropertyChanged(null);
            _taskBarInfoService.ResetTaskBarInfo(false);
        }

        private bool IsProjectItemEnabledForActions()
        {
            return (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !SelectedProjectItem.IsBatchBuildProject);
        }

        private void CopyErrorMessageToClipboard(ProjectItem projectItem)
        {
            try
            {
                var errors = new StringBuilder();
                foreach (var errorItem in projectItem.Errors)
                {
                    errors.AppendLine(string.Format("{0}({1},{2},{3},{4}): error {5}: {6}", errorItem.File, errorItem.LineNumber, errorItem.ColumnNumber, errorItem.EndLineNumber, errorItem.EndColumnNumber, errorItem.Code, errorItem.Message));
                }
                Clipboard.SetText(errors.ToString());
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        #region Commands

        public ICommand NavigateToErrorCommand => new RelayCommand(obj => _errorNavigationService.NavigateToErrorItem(obj as ErrorItem));

        public ICommand ReportIssues => new RelayCommand(obj => GithubHelper.OpenBrowserWithPrefilledIssue());

        public ICommand GridSorting => new RelayCommand(obj => ReorderGrid(obj));

        public ICommand GridGroupPropertyMenuItemClicked => new RelayCommand(obj => GridGroupPropertyName = (obj != null) ? obj.ToString() : string.Empty);

        public ICommand SelectedProjectOpenContainingFolderAction => new RelayCommand(obj => OpenContainingFolder(), canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.FullName)));

        public ICommand SelectedProjectCopyBuildOutputFilesToClipboardAction => new RelayCommand(obj => _buildService.ProjectCopyBuildOutputFilesToClipBoard(SelectedProjectItem), canExecute: obj => (SelectedProjectItem != null && !string.IsNullOrEmpty(SelectedProjectItem.UniqueName) && !ControlSettings.ProjectItemSettings.CopyBuildOutputFileTypesToClipboard.IsEmpty));

        public ICommand SelectedProjectBuildAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.BuildCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectRebuildAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.RebuildCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectCleanAction => new RelayCommand(obj => _buildService.RaiseCommandForSelectedProject(SelectedProjectItem, (int)VSConstants.VSStd97CmdID.CleanCtx), canExecute: obj => IsProjectItemEnabledForActions());

        public ICommand SelectedProjectCopyErrorMessagesAction => new RelayCommand(obj => CopyErrorMessageToClipboard(SelectedProjectItem), canExecute: obj => SelectedProjectItem?.ErrorsCount > 0);

        public ICommand BuildSolutionAction => new RelayCommand(obj => _buildService.BuildSolution());

        public ICommand RebuildSolutionAction => new RelayCommand(obj => _buildService.RebuildSolution());

        public ICommand CleanSolutionAction => new RelayCommand(obj => _buildService.CleanSolution());

        public ICommand CancelBuildSolutionAction => new RelayCommand(obj => _buildService.CancelBuildSolution());

        public ICommand OpenGridColumnsSettingsAction => new RelayCommand(obj => ShowOptionPage?.Invoke(typeof(GridSettings))); 

        public ICommand OpenGeneralSettingsAction => new RelayCommand(obj => ShowOptionPage?.Invoke(typeof(GeneralSettings)));

        #endregion

        public event Action<Type> ShowOptionPage;
    }
}
