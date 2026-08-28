using System.Runtime.InteropServices;

namespace EZManifest.Services;

/// <summary>
/// Vista+ folder picker with Ctrl/Shift multi-select (WinUI FolderPicker is single-only).
/// </summary>
internal static class NativeFolderOpenDialog
{
    private const int FosAllowMultiSelect = 0x00000200;
    private const int FosPickFolders = 0x00000020;
    private const int FosForceFileSystem = 0x00000040;
    private const int FosPathMustExist = 0x00000800;
    private const int FosFileMustExist = 0x00001000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public static IReadOnlyList<string>? PickFolders(nint ownerHwnd)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            // Preserve defaults, then enable folder multi-select (replacing options breaks multi-OK).
            dialog.GetOptions(out int options);
            options |= FosAllowMultiSelect | FosPickFolders | FosForceFileSystem | FosPathMustExist;
            options &= ~FosFileMustExist; // file-must-exist blocks multi-folder confirm on some builds
            dialog.SetOptions(options);
            dialog.SetTitle("Select folders");

            int hr = dialog.Show(ownerHwnd);
            if (hr == ErrorCancelled || hr != 0)
                return null;

            var paths = new List<string>();

            if (dialog.GetResults(out IShellItemArray? results) == 0 && results is not null)
            {
                if (results.GetCount(out int count) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (results.GetItemAt(i, out IShellItem? item) != 0 || item is null)
                            continue;

                        TryAddPath(paths, item);
                    }
                }
            }

            // Fallback: single selection path if GetResults yielded nothing.
            if (paths.Count == 0
                && dialog.GetResult(out IShellItem? single) == 0
                && single is not null)
            {
                TryAddPath(paths, single);
            }

            return paths.Count == 0 ? null : paths;
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static void TryAddPath(List<string> paths, IShellItem item)
    {
        if (item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr namePtr) != 0 || namePtr == IntPtr.Zero)
            return;

        try
        {
            string? path = Marshal.PtrToStringUni(namePtr);
            if (!string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path)
                && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(int fos);
        [PreserveSig] int GetOptions(out int pfos);
        [PreserveSig] int SetDefaultFolder(IShellItem psi);
        [PreserveSig] int SetFolder(IShellItem psi);
        [PreserveSig] int GetFolder(out IShellItem ppsi);
        [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName(out IntPtr pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        [PreserveSig] int AddPlace(IShellItem psi, int alignment);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr pFilter);
        [PreserveSig] int GetResults(out IShellItemArray ppenum);
        [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out int pdwNumItems);
        [PreserveSig] int GetItemAt(int dwIndex, out IShellItem ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }
}
