namespace Yamv.Core.Theming;

/// <summary>Moves a file to the platform's Recycle Bin or Trash, where the user can restore it.</summary>
public interface IFileTrash
{
    /// <summary>Moves the file at <paramref name="path"/> to the Recycle Bin or Trash.</summary>
    void MoveToTrash(string path);
}
