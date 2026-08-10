using BetterDAM.Core.Models;

namespace BetterDAM.Core.Interfaces;

public interface IFolderBrowser
{
    IReadOnlyList<FolderNode> GetRoots();

    IReadOnlyList<FolderNode> GetSubfolders(string path);
}
