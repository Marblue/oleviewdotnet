﻿//    This file is part of OleViewDotNet.
//    Copyright (C) James Forshaw 2014
//
//    OleViewDotNet is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    OleViewDotNet is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with OleViewDotNet.  If not, see <http://www.gnu.org/licenses/>.

using NtApiDotNet;
using OleViewDotNet.Database;
using OleViewDotNet.Interop;
using OleViewDotNet.Processes;
using OleViewDotNet.Proxy;
using OleViewDotNet.Security;
using OleViewDotNet.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace OleViewDotNet.Forms;

public partial class MainForm : Form
{
    private readonly DockPanel   m_dockPanel;
    private readonly COMRegistry m_registry;
    private PropertyGrid m_property_grid;

    private void UpdateTitle()
    {
        Text = $"OleView .NET v{COMUtilities.GetVersion()}";
        if (COMUtilities.IsAdministrator())
        {
            Text += " - Administrator";
            menuFileOpenAsAdmin.Visible = false;
        }

        if (Environment.Is64BitOperatingSystem)
        {
            if (!Environment.Is64BitProcess)
            {
                Text += " - 32bit";
            }
            else
            {
                Text += $" - 64bit ({RuntimeInformation.ProcessArchitecture})";
            }
        }

        if (m_registry.FilePath != null)
        {
            Text += $" - {m_registry.FilePath}";
        }
    }

    public MainForm(COMRegistry registry)
    {
        m_registry = registry;
        InitializeComponent();
        m_dockPanel = new DockPanel();
        m_dockPanel.ActiveAutoHideContent = null;
        m_dockPanel.Dock = DockStyle.Fill;
        m_dockPanel.Name = "dockPanel";
        Controls.Add(m_dockPanel);
        m_dockPanel.BringToFront();
        CreatePropertyGrid(true);

        if (Environment.Is64BitProcess)
        {
            bool is_arm64 = NtSystemInfo.EmulationProcessorInformation.ProcessorArchitecture == ProcessorAchitecture.ARM;
            bool is_amd64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            menuFileOpenARM64Viewer.Visible = is_arm64 && is_amd64;
            menuFileOpen64BitViewer.Visible = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            menuFileOpen32BitViewer.Visible = true;
        }
        else
        {
            menuFileOpen32BitViewer.Visible = false;
            menuFileOpenARM64Viewer.Visible = false;
            menuFileOpen64BitViewer.Visible = false;
        }

        UpdateTitle();
    }

    private void MainForm_Load(object sender, EventArgs e)
    {
        HostControl(new RegistryPropertiesControl(m_registry));
    }

    private void menuFileExit_Click(object sender, EventArgs e)
    {
        Close();
    }

    public void HostControl(Control c)
    {
        HostControl(c, null);
    }

    public void HostControl(Control c, string name)
    {
        DocumentForm frm = new(c)
        {
            ShowHint = DockState.Document
        };
        frm.Show(m_dockPanel);
        if (!string.IsNullOrWhiteSpace(name))
        {
            frm.TabText = name;
        }
    }

    public async Task HostObject(ICOMClassEntry ent, object obj, bool factory)
    {
        Dictionary<string, string> props = new();

        ent ??= new COMCLSIDEntry(m_registry, Guid.Empty, COMServerType.UnknownServer);
        props.Add("CLSID", ent.Clsid.FormatGuid());
        props.Add("Name", ent.Name);
        props.Add("Server", ent.DefaultServer);

        /* Need to implement a type library reader */
        Type dispType = COMUtilities.GetDispatchTypeInfo(this, obj);

        if (!ent.InterfacesLoaded)
        {
            await ent.LoadSupportedInterfacesAsync(false, null);
        }

        IEnumerable<COMInterfaceInstance> intfs = factory ? ent.FactoryInterfaces : ent.Interfaces;

        ObjectInformation view = new(m_registry, ent, ent.Name, obj,
            props, intfs.Select(i => m_registry.MapIidToInterface(i.Iid)).ToArray());
        HostControl(view);
    }

    private void OpenView(COMRegistryViewer.DisplayMode mode, IEnumerable<COMProcessEntry> processes)
    {
        Cursor currCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        HostControl(new COMRegistryViewer(m_registry, mode, processes));
        Cursor.Current = currCursor;
    }

    private void OpenView(COMRegistryViewer.DisplayMode mode)
    {
        OpenView(mode, null);
    }

    private void menuViewCLSIDs_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.CLSIDs);
    }

    private void menuViewCLSIDsByName_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.CLSIDsByName);
    }

    private void menuViewProgIDs_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.ProgIDs);
    }

    private void menuViewCLSIDsByServer_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.CLSIDsByServer);
    }

    private void menuViewInterfaces_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.Interfaces);
    }

    private void menuViewInterfacesByName_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.InterfacesByName);
    }

    private void menuViewROT_Click(object sender, EventArgs e)
    {
        HostControl(new ROTViewer(m_registry));
    }

    private void menuViewImplementedCategories_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.ImplementedCategories);
    }

    private void menuViewPreApproved_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.PreApproved);
    }

    public async Task CreateInstanceFromCLSID(Guid clsid, CLSCTX clsctx, bool class_factory)
    {
        try
        {
            COMCLSIDEntry ent = null;
            Dictionary<string, string> props = new();
            object comObj = null;
            string strObjName = "";
            IEnumerable<COMInterfaceEntry> ints = null;

            if (m_registry.Clsids.ContainsKey(clsid))
            {
                ent = m_registry.Clsids[clsid];
                strObjName = ent.Name;
                props.Add("CLSID", ent.Clsid.FormatGuid());
                props.Add("Name", ent.Name);
                props.Add("Server", ent.DefaultServer);
                await ent.LoadSupportedInterfacesAsync(false, null);

                if (class_factory)
                {
                    comObj = ent.CreateClassFactory();
                    ints = ent.FactoryInterfaces.Select(i => m_registry.MapIidToInterface(i.Iid));
                }
                else
                {
                    comObj = ent.CreateInstanceAsObject(clsctx, null);
                    ints = ent.Interfaces.Select(i => m_registry.MapIidToInterface(i.Iid));
                }
            }
            else
            {
                IntPtr pObj;
                int hr;

                if (class_factory)
                {
                    hr = NativeMethods.CoGetClassObject(clsid, clsctx, null, COMInterfaceEntry.IID_IUnknown, out pObj);
                }
                else
                {
                    hr = NativeMethods.CoCreateInstance(clsid, IntPtr.Zero, clsctx,
                                COMInterfaceEntry.IID_IUnknown, out pObj);
                }

                if (hr != 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    ints = m_registry.GetInterfacesForIUnknown(pObj).ToArray();
                    comObj = Marshal.GetObjectForIUnknown(pObj);
                    strObjName = clsid.FormatGuid();
                    props.Add("CLSID", clsid.FormatGuid());
                }
                finally
                {
                    Marshal.Release(pObj);
                }
            }

            if (comObj != null)
            {
                /* Need to implement a type library reader */
                Type dispType = COMUtilities.GetDispatchTypeInfo(this, comObj);

                HostControl(new ObjectInformation(m_registry, ent, strObjName, comObj, props, ints.ToArray()));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void menuViewCreateInstanceFromCLSID_Click(object sender, EventArgs e)
    {
        using CreateCLSIDForm frm = new();
        if (frm.ShowDialog() == DialogResult.OK)
        {
            await CreateInstanceFromCLSID(frm.Clsid, frm.ClsCtx, frm.ClassFactory);
        }
    }

    private void menuViewCLSIDsByLocalServer_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.CLSIDsByLocalServer);
    }

    private void menuViewIELowRights_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.IELowRights);
    }

    private void menuViewLocalServices_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.LocalServices);
    }

    private void menuViewAppIDs_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.AppIDs);
    }

    private async void menuObjectFromMarshalledStream_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dlg = new();
        dlg.Filter = "All Files (*.*)|*.*";

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                byte[] data = File.ReadAllBytes(dlg.FileName);
                object comObj = COMUtilities.UnmarshalObject(data);
                await OpenObjectInformation(comObj, string.Format("Unmarshalled {0}", Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public async Task OpenObjectInformation(object comObj, string defaultName)
    {
        if (comObj != null)
        {
            COMCLSIDEntry ent = null;
            Dictionary<string, string> props = new();
            string strObjName = "";
            IEnumerable<COMInterfaceEntry> ints = null;
            Guid clsid = COMUtilities.GetObjectClass(comObj);

            if (m_registry.Clsids.ContainsKey(clsid))
            {
                ent = m_registry.Clsids[clsid];
                strObjName = ent.Name;
                props.Add("CLSID", ent.Clsid.FormatGuid());
                props.Add("Name", ent.Name);
                props.Add("Server", ent.DefaultServer);
                await ent.LoadSupportedInterfacesAsync(false, null);
                ints = ent.Interfaces.Select(i => m_registry.MapIidToInterface(i.Iid));
            }
            else
            {
                ints = m_registry.GetInterfacesForObject(comObj).ToArray();
                strObjName = defaultName ?? clsid.FormatGuid();
                props.Add("CLSID", clsid.FormatGuid());
            }

            Type dispType = COMUtilities.GetDispatchTypeInfo(this, comObj);
            HostControl(new ObjectInformation(m_registry, ent, strObjName, comObj, props, ints.ToArray()));
        }
    }

    private async void menuObjectFromSerializedStream_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dlg = new();
        dlg.Filter = "All Files (*.*)|*.*";

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                using Stream stm = dlg.OpenFile();
                object obj = COMUtilities.OleLoadFromStream(stm, out Guid clsid);
                await HostObject(m_registry.MapClsidToEntry(clsid), obj, obj is IClassFactory);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void menuHelpAbout_Click(object sender, EventArgs e)
    {
        using AboutForm frm = new();
        frm.ShowDialog(this);
    }

    private void menuRegistryTypeLibs_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.Typelibs);
    }

    private void menuRegistryAppIDsIL_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.AppIDsWithIL);
    }

    private void menuViewCLSIDsWithSurrogate_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.CLSIDsWithSurrogate);
    }

    private void menuFileOpen32BitViewer_Click(object sender, EventArgs e)
    {
        try
        {
            COMUtilities.StartArchProcess(Architecture.X86, string.Empty);
        }
        catch(Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string _last_moniker = "Specify Moniker";

    private async Task ParseOrBindMoniker(bool bind)
    {
        using BuildMonikerForm frm = new(_last_moniker);
        if (frm.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _last_moniker = frm.MonikerString;
                object comObj = frm.Moniker;
                if (bind)
                {
                    Guid iid = COMInterfaceEntry.IID_IUnknown;
                    frm.Moniker.BindToObject(frm.BindContext, null, ref iid, out comObj);
                }

                if (comObj != null)
                {
                    await OpenObjectInformation(comObj, _last_moniker);
                }
            }
            catch (Exception ex)
            {
                EntryPoint.ShowError(this, ex);
            }
        }
    }
    
    private async void menuObjectBindMoniker_Click(object sender, EventArgs e)
    {
        await ParseOrBindMoniker(true);
    }

    private string GetSaveFileName(bool save)
    {
        if (save && !string.IsNullOrWhiteSpace(m_registry.FilePath))
        {
            return m_registry.FilePath;
        }

        using (SaveFileDialog dlg = new())
        {
            if (!string.IsNullOrWhiteSpace(m_registry.FilePath))
            {
                dlg.FileName = m_registry.FilePath;
            }

            dlg.Filter = "OleViewDotNet DB File (*.ovdb)|*.ovdb|All Files (*.*)|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                return dlg.FileName;
            }
        }

        return string.Empty;
    }

    private void SaveDatabase(bool save)
    {
        string filename = GetSaveFileName(save);
        if (string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        try
        {
            m_registry.Save(filename);
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void menuFileSaveDatabase_Click(object sender, EventArgs e)
    {
        SaveDatabase(true);
    }

    private void menuFileOpenDatabase_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dlg = new();
        dlg.Filter = "OleViewDotNet DB File (*.ovdb)|*.ovdb|All Files (*.*)|*.*";
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadRegistry(() => COMUtilities.LoadRegistry(this, dlg.FileName));
        }
    }

    private void menuRegistryMimeTypes_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.MimeTypes);
    }

    private void menuRegistryAppIDsWithAC_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.AppIDsWithAC);
    }

    private void menuSecurityDefaultAccess_Click(object sender, EventArgs e)
    {
        COMSecurity.ViewSecurity(m_registry, "Default Access", m_registry.DefaultAccessPermission, true);
    }

    private void menuSecurityDefaultAccessRestriction_Click(object sender, EventArgs e)
    {
        COMSecurity.ViewSecurity(m_registry, "Default Access Restrictions", m_registry.DefaultAccessRestriction, true);
    }

    private void menuSecurityDefaultLaunch_Click(object sender, EventArgs e)
    {
        COMSecurity.ViewSecurity(m_registry, "Default Launch", m_registry.DefaultLaunchPermission, false);
    }

    private void menuSecurityDefaultLaunchRestriction_Click(object sender, EventArgs e)
    {
        COMSecurity.ViewSecurity(m_registry, "Default Launch Restrictions", m_registry.DefaultLaunchRestriction, false);
    }

    public COMRegistry Registry => m_registry;

    private void menuRegistryProperties_Click(object sender, EventArgs e)
    {
        HostControl(new RegistryPropertiesControl(m_registry));
    }

    private void LoadRegistry(Func<COMRegistry> loader)
    {
        try
        {
            new MainForm(loader()).Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void menuFileOpenMachineOnly_Click(object sender, EventArgs e)
    {
        LoadRegistry(() => COMUtilities.LoadRegistry(this, COMRegistryMode.MachineOnly));
    }

    private void menuFileOpenUserOnly_Click(object sender, EventArgs e)
    {
        LoadRegistry(() => COMUtilities.LoadRegistry(this, COMRegistryMode.UserOnly));
    }

    private void menuFileDiff_Click(object sender, EventArgs e)
    {
        using DiffRegistryForm frm = new(m_registry);
        if (frm.ShowDialog(this) == DialogResult.OK)
        {
            new MainForm(frm.DiffRegistry).Show();
        }
    }

    private async void menuObjectFromFile_Click(object sender, EventArgs e)
    {
        try
        {
            using OpenFileDialog dlg = new();
            dlg.Filter = "All Files (*.*)|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                COMCLSIDEntry entry = m_registry.GetFileClass(dlg.FileName);
                if (entry != null)
                {
                    IPersistFile ps = (IPersistFile)entry.CreateInstanceAsObject(entry.CreateContext, null);
                    ps.Load(dlg.FileName, (int)STGM.READ);
                    await HostObject(entry, ps, false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void menuObjectParseMoniker_Click(object sender, EventArgs e)
    {
        await ParseOrBindMoniker(false);
    }

    private void OpenHexEditor(string name, byte[] bytes)
    {
        ObjectHexEditor editor = new(m_registry, name, bytes);
        HostControl(editor);
    }

    private void menuHexEditorFromFile_Click(object sender, EventArgs e)
    {
        try
        {
            using OpenFileDialog dlg = new();
            dlg.Multiselect = true;
            dlg.Filter = "All Files (*.*)|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                foreach (string file in dlg.FileNames)
                {
                    OpenHexEditor(Path.GetFileName(file),
                        File.ReadAllBytes(file));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void menuHexEditorEmpty_Click(object sender, EventArgs e)
    {
        OpenHexEditor("Empty", new byte[0]);
    }

    private void menuFileOpenTypeLib_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dlg = new();
        dlg.Filter = "TLB Files (*.tlb)|*.tlb|Executable Files (*.exe;*.dll;*.ocx)|*.exe;*.dll;*.ocx|All Files (*.*)|*.*";
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            Assembly typelib = COMUtilities.LoadTypeLib(this, dlg.FileName);
            if (typelib != null)
            {
                HostControl(new TypeLibControl(Path.GetFileName(dlg.FileName), typelib, Guid.Empty, false));
            }
        }
    }

    private void menuRegistryInterfaceProxies_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.ProxyCLSIDs);
    }

    private void menuFileOpenProxyDll_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dlg = new();
        dlg.Filter = "Executable Files (*.dll;*.ocx)|*.dll;*.ocx|All Files (*.*)|*.*";
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                using var resolver = EntryPoint.GetProxyParserSymbolResolver();
                COMProxyInstance proxy = COMProxyInstance.GetFromFile(dlg.FileName, resolver, m_registry);
                HostControl(new TypeLibControl(m_registry, Path.GetFileName(dlg.FileName), proxy, Guid.Empty));
            }
            catch (Exception ex)
            {
                EntryPoint.ShowError(this, ex);
            }
        }
    }

    private void menuFileQueryAllInterfaces_Click(object sender, EventArgs e)
    {
        using QueryInterfacesOptionsForm options = new();
        if (options.ShowDialog(this) == DialogResult.OK)
        {
            COMUtilities.QueryAllInterfaces(this, m_registry.Clsids.Values,
                options.ServerTypes, options.ConcurrentQueries,
                options.RefreshInterfaces);
        }
    }

    private void menuFileSaveAsDatabase_Click(object sender, EventArgs e)
    {
        SaveDatabase(false);
    }

    private void menuFileSettings_Click(object sender, EventArgs e)
    {
        using SettingsForm frm = new();
        frm.ShowDialog(this);
    }

    private static bool _configured_symbols = false;

    private void ConfigureSymbols()
    {
        if (_configured_symbols)
        {
            return;
        }

        _configured_symbols = true;

        if (!Properties.Settings.Default.SymbolsConfigured)
        {
            if (MessageBox.Show(this, "Symbol support has not been configured, would you like to do that now?",
                "Configure Symbols", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                using SettingsForm frm = new();
                frm.ShowDialog(this);
            }
        }

        COMUtilities.SetupCachedSymbols();
    }

    private void LoadProcesses<TKey>(Func<COMProcessEntry, TKey> orderby_selector)
    {
        ConfigureSymbols();
        IEnumerable<COMProcessEntry> processes = COMUtilities.LoadProcesses(this, m_registry);
        if (processes != null && processes.Any())
        {
            OpenView(COMRegistryViewer.DisplayMode.Processes, processes.OrderBy(orderby_selector));
        }
    }

    internal void LoadIPid(Guid ipid)
    {
        try
        {
            ConfigureSymbols();
            var proc = COMUtilities.LoadProcesses(new int[] { COMUtilities.GetProcessIdFromIPid(ipid) }, this, m_registry).FirstOrDefault();
            if (proc != null)
            {
                COMIPIDEntry ipid_entry = proc.Ipids.Where(e => e.Ipid == ipid).FirstOrDefault();
                if (ipid_entry != null)
                {
                    HostControl(new PropertiesControl(m_registry, string.Format("IPID: {0}", ipid.FormatGuid()), ipid_entry));
                }
                else
                {
                    throw new Exception("Couldn't find the target IPID in the remote process. Did you configure the Dbghelp and symbol paths correctly in the main settings?");
                }
            }
            else
            {
                throw new Exception($"Couldn't load process for IPID: {ipid.FormatGuid()}");
            }
        }
        catch (Exception ex)
        {
            EntryPoint.ShowError(this, ex);
        }
    }

    internal void LoadProcessByProcessId(int pid)
    {
        try
        {
            ConfigureSymbols();
            var processes = COMUtilities.LoadProcesses(new int[] { pid }, this, m_registry);
            if (!processes.Any())
            {
                throw new ArgumentException(string.Format("Process {0} has not initialized COM, or is inaccessible", pid));
            }

            HostControl(new PropertiesControl(m_registry, string.Format("Process {0}", pid), processes.First()));
        }
        catch (Exception ex)
        {
            EntryPoint.ShowError(this, ex);
        }
    }

    private void menuProcessesAllProcessesByPid_Click(object sender, EventArgs e)
    {
        LoadProcesses(p => p.ProcessId);
    }

    private void menuProcessesAllProcessesByName_Click(object sender, EventArgs e)
    {
        LoadProcesses(p => p.Name);
    }

    private void menuProcessesAllProcessesByUser_Click(object sender, EventArgs e)
    {
        LoadProcesses(p => p.User);
    }

    private void menuFileOpenAsAdmin_Click(object sender, EventArgs e)
    {
        try
        {
            ProcessStartInfo start_info = new(COMUtilities.GetExePath());
            start_info.UseShellExecute = true;
            start_info.Verb = "runas";

            Process.Start(start_info).Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void UpdatePropertyGrid(object obj)
    {
        if (m_property_grid != null && !m_property_grid.IsDisposed)
        {
            m_property_grid.SelectedObject = obj;
        }
    }

    public void CreatePropertyGrid(bool autohide)
    {
        if (m_property_grid == null || m_property_grid.IsDisposed)
        {
            m_property_grid = new PropertyGrid();
            m_property_grid.ToolbarVisible = false;
            m_property_grid.PropertySort = PropertySort.Alphabetical;
            
            DocumentForm frm = new(m_property_grid);
            frm.TabText = "Object Properties";
            frm.ShowHint = autohide ? DockState.DockRightAutoHide : DockState.DockRight;
            frm.Show(m_dockPanel);
        }
    }

    private void menuObjectPropertiesViewer_Click(object sender, EventArgs e)
    {
        CreatePropertyGrid(false);
    }

    private static STGM GetStorageAccess(bool read_only)
    {
        if (read_only)
        {
            return STGM.READ | STGM.SHARE_DENY_WRITE;
        }
        else
        {
            return STGM.SHARE_EXCLUSIVE | STGM.READWRITE;
        }
    }

    private const string STORAGE_FILTER = "All Files (*.*)|*.*|Doc Files (*.doc)|*.doc";

    private void menuStorageOpenStorage_Click(object sender, EventArgs e)
    {
        try
        {
            using OpenFileDialog dlg = new();
            dlg.ShowReadOnly = true;
            dlg.ReadOnlyChecked = true;
            dlg.Filter = STORAGE_FILTER;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                IStorage stg = NativeMethods.StgOpenStorage(dlg.FileName, null, GetStorageAccess(dlg.ReadOnlyChecked), IntPtr.Zero, 0);

                HostControl(new StorageViewer(stg, Path.GetFileName(dlg.FileName), dlg.ReadOnlyChecked));
            }
        }
        catch (Exception ex)
        {
            EntryPoint.ShowError(this, ex, true);
        }
    }

    private void menuStorageNewStorage_Click(object sender, EventArgs e)
    {
        try
        {
            using SaveFileDialog dlg = new();
            dlg.Filter = STORAGE_FILTER;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                Guid iid = typeof(IStorage).GUID;
                IStorage stg = NativeMethods.StgCreateStorageEx(dlg.FileName,
                    STGM.SHARE_EXCLUSIVE | STGM.READWRITE, STGFMT.Storage, 0, null, IntPtr.Zero, iid);
                HostControl(new StorageViewer(stg, Path.GetFileName(dlg.FileName), false));
            }
        }
        catch (Exception ex)
        {
            EntryPoint.ShowError(this, ex, true);
        }
    }

    private void menuRegistryRuntimeClasses_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.RuntimeClasses);
    }

    private void menuProcessesSelectProcess_Click(object sender, EventArgs e)
    {
        using SelectProcessForm form = new(ProcessAccessRights.VmRead, false, true);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            LoadProcessByProcessId(form.SelectedProcess.ProcessId);
        }
    }

    private void menuRegistryRuntimeServers_Click(object sender, EventArgs e)
    {
        OpenView(COMRegistryViewer.DisplayMode.RuntimeServers);
    }

    private void menuFileOpenPowershell_Click(object sender, EventArgs e)
    {
        try
        {
            string temp_file = Path.GetTempFileName();
            m_registry.Save(temp_file);

            string startup_script = Path.Combine(COMUtilities.GetAppDirectory(), "Startup-Module.ps1");
            if (!File.Exists(startup_script))
            {
                throw new ArgumentException("PowerShell startup script is missing");
            }

            using (Process.Start("powershell.exe", $"-NoExit -ExecutionPolicy Bypass -File \"{startup_script}\" \"{temp_file}\" -DeleteFile"))
            {
            }
        }
        catch (Exception ex)
        {
            EntryPoint.ShowError(this, ex);
        }
    }

    private void FocusOnTextBoxFilter()
    {
        // user hit search so we set the input focus into the first inputbox (which is usually a filter input)
        if (m_dockPanel.ActivePane.ActiveContent is not DocumentForm df) return;

        Control[] textBoxFilters = df.Controls.Find("textBoxFilter", true);
        if (textBoxFilters.Length != 1) return;

        textBoxFilters[0].Focus();
    }

    private void MainForm_KeyUp(object sender, KeyEventArgs e)
    {
        if(e.Control&&(e.KeyCode == Keys.W))
        {
            m_dockPanel.ActivePane.CloseActiveContent();
            return;
        }

        if (e.Control && e.Alt && (e.KeyCode == Keys.Left))
        {
            m_dockPanel.NavigateDocument(DockPanelHelper.Direction.Left);
            return;
        }


        if (e.Control && e.Alt && (e.KeyCode == Keys.Right))
        {
            m_dockPanel.NavigateDocument(DockPanelHelper.Direction.Right);
            return;
        }

        if (e.Control && (e.KeyCode == Keys.F))
        {
            FocusOnTextBoxFilter();
            return;
        }
    }

    private void menuFileOpen64BitViewer_Click(object sender, EventArgs e)
    {
        try
        {
            COMUtilities.StartArchProcess(Architecture.X64, string.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void menuFileOpenARM64Viewer_Click(object sender, EventArgs e)
    {
        try
        {
            COMUtilities.StartArchProcess(Architecture.Arm64, string.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
