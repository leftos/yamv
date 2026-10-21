using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yamv.Core.Documents;

namespace Yamv.App.ViewModels;

/// <summary>One row of a document window's tree.</summary>
/// <param name="name">What the row shows.</param>
/// <param name="path">The full file-system path the row stands for.</param>
public abstract partial class TreeItemViewModel(string name, string path) : ViewModelBase
{
    /// <summary>What the row shows.</summary>
    public string Name { get; } = name;

    /// <summary>The full file-system path the row stands for.</summary>
    public string Path { get; } = path;

    /// <summary>True when this row is the window's selected document.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A top-level group row: a git work-tree root or a plain folder.</summary>
public sealed class GroupItemViewModel : TreeItemViewModel
{
    private readonly Action<string, bool> _setExpanded;
    private bool _isExpanded;

    /// <summary>Creates the row for <paramref name="item"/>.</summary>
    /// <param name="item">The group as the window state's tree holds it.</param>
    /// <param name="children">The rows directly below the group.</param>
    /// <param name="setExpanded">Records a user's expand or collapse of the group, by group path.</param>
    /// <param name="closeGroup">Closes every document of the group, by group path.</param>
    public GroupItemViewModel(GroupItem item, IReadOnlyList<TreeItemViewModel> children, Action<string, bool> setExpanded, Action<string> closeGroup)
        : base(item.Name, item.Path)
    {
        _setExpanded = setExpanded;
        _isExpanded = item.IsExpanded;
        IsRepo = item.IsRepo;
        Children = children;
        CloseGroupCommand = new RelayCommand(() => closeGroup(Path));
    }

    /// <summary>True when the group is a git work-tree root, false when it is a plain folder.</summary>
    public bool IsRepo { get; }

    /// <summary>The folder and document rows directly below the group.</summary>
    public IReadOnlyList<TreeItemViewModel> Children { get; }

    /// <summary>True when the group's children are shown. Setting it records the choice in the window state.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                _setExpanded(Path, value);
            }
        }
    }

    /// <summary>Closes every document of the group.</summary>
    public IRelayCommand CloseGroupCommand { get; }
}

/// <summary>A folder row below a group.</summary>
/// <param name="item">The folder as the window state's tree holds it.</param>
/// <param name="children">The rows directly below the folder.</param>
public sealed class FolderItemViewModel(FolderItem item, IReadOnlyList<TreeItemViewModel> children) : TreeItemViewModel(item.Name, item.Path)
{
    /// <summary>The folder and document rows directly below the folder.</summary>
    public IReadOnlyList<TreeItemViewModel> Children { get; } = children;
}

/// <summary>A document row.</summary>
public sealed class DocumentItemViewModel : TreeItemViewModel
{
    /// <summary>Creates the row for <paramref name="item"/>.</summary>
    /// <param name="item">The document as the window state's tree holds it.</param>
    /// <param name="isSelected">True when the document is the window's selection.</param>
    /// <param name="close">Closes a document, by path.</param>
    public DocumentItemViewModel(DocumentItem item, bool isSelected, Action<string> close)
        : base(item.Name, item.Path)
    {
        IsMissing = item.IsMissing;
        IsSelected = isSelected;
        CloseCommand = new RelayCommand(() => close(Path));
    }

    /// <summary>True when the document is no longer on disk.</summary>
    public bool IsMissing { get; }

    /// <summary>Closes the document.</summary>
    public IRelayCommand CloseCommand { get; }
}
