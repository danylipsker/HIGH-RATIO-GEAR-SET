// Gear Set add-in for SOLIDWORKS 2020+
//
// Adds a "Gear Set" CommandManager tab / toolbar / menu with
//   * Create gear set...  - module and gear ratio in, pick one of the exact tooth-count solutions,
//                           see its overall dimensions, build the six parts and the assembly as
//                           native SOLIDWORKS files (nothing else is written)
//   * Open output folder  - Explorer on the last folder written
//
// Registration (install.ps1 via RegAsm /codebase):
//   HKLM\SOFTWARE\SolidWorks\Addins\{GUID}          Title, Description, (Default)=0
//   HKCU\Software\SolidWorks\AddInsStartup\{GUID}   (Default)=1  -> load at startup
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace GearSet
{
    [Guid(AddInGuid), ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    [ProgId("GearSet.AddIn")]
    public class GearSetAddin : ISwAddin
    {
        public const string AddInGuid = "3E6F2B1A-9C4D-4E8A-B7F1-2D5C8A9E0B47";
        public const string Title = "Gear Set";
        public const string Description = "High-ratio single-planet 3K planetary reducer: enter module and ratio, pick a tooth set, build parts and assembly";

        private const int GroupId = 1208;
        private const int CmdCreate = 0;
        private const int CmdFolder = 1;

        private ISldWorks _sw;
        private int _cookie;
        private ICommandManager _cmdMgr;
        private CommandGroup _group;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            Log("ConnectToSW (cookie " + Cookie + ")");
            try
            {
                _sw = (ISldWorks)ThisSW;
                _cookie = Cookie;
                _sw.SetAddinCallbackInfo2(0, this, _cookie);
                _cmdMgr = _sw.GetCommandManager(_cookie);
                AddCommands();
                Log("connected to SOLIDWORKS " + _sw.RevisionNumber());
            }
            catch (Exception ex) { Log("ERROR in ConnectToSW: " + ex); }
            return true;
        }

        public bool DisconnectFromSW()
        {
            try { RemoveCommands(); } catch { /* ignore */ }
            if (_cmdMgr != null) { Marshal.ReleaseComObject(_cmdMgr); _cmdMgr = null; }
            if (_sw != null) { Marshal.ReleaseComObject(_sw); _sw = null; }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        /// <summary>Diagnostics: %TEMP%\GearSetAddin.log</summary>
        public static void Log(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "GearSetAddin.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { /* ignore */ }
        }

        // ------------------------------------------------------------------ commands
        private void AddCommands()
        {
            int errors = 0;
            _group = _cmdMgr.CreateCommandGroup2(GroupId, Title, "High-ratio gear set", "Create a high-ratio planetary gear set", -1, true, ref errors);
            if (_group == null) throw new InvalidOperationException("CreateCommandGroup2 failed, error " + errors);

            string iconDir = Path.Combine(Path.GetDirectoryName(typeof(GearSetAddin).Assembly.Location) ?? "", "icons");
            int[] sizes = { 20, 32, 40, 64, 96, 128 };
            var cmdIcons = new List<string>();
            var mainIcons = new List<string>();
            foreach (int s in sizes)
            {
                cmdIcons.Add(Path.Combine(iconDir, "cmd" + s + ".png"));
                mainIcons.Add(Path.Combine(iconDir, "main" + s + ".png"));
            }
            if (File.Exists(cmdIcons[0]))
            {
                _group.IconList = cmdIcons.ToArray();
                _group.MainIconList = mainIcons.ToArray();
            }
            int menuAndToolbar = (int)swCommandItemType_e.swMenuItem | (int)swCommandItemType_e.swToolbarItem;
            _group.AddCommandItem2("Create gear set...", -1, "Module and ratio in, parts and assembly out",
                "Create gear set", 0, "CreateGearSet", "EnableAlways", CmdCreate, menuAndToolbar);
            _group.AddCommandItem2("Open output folder", -1, "Show the last folder the gear set was written to",
                "Open folder", 1, "OpenOutputFolder", "EnableAlways", CmdFolder, menuAndToolbar);
            _group.HasToolbar = true;
            _group.HasMenu = true;
            _group.Activate();

            int[] docTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };
            foreach (int docType in docTypes)
            {
                ICommandTab tab = _cmdMgr.GetCommandTab(docType, Title);
                if (tab != null) _cmdMgr.RemoveCommandTab((CommandTab)tab);
                tab = _cmdMgr.AddCommandTab(docType, Title);
                if (tab == null) continue;
                ICommandTabBox box = tab.AddCommandTabBox();
                int below = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                box.AddCommands(new[] { _group.get_CommandID(CmdCreate), _group.get_CommandID(CmdFolder) }, new[] { below, below });
            }
        }

        private void RemoveCommands()
        {
            if (_cmdMgr == null) return;
            int[] docTypes = { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING };
            foreach (int docType in docTypes)
            {
                ICommandTab tab = _cmdMgr.GetCommandTab(docType, Title);
                if (tab != null) _cmdMgr.RemoveCommandTab((CommandTab)tab);
            }
            _cmdMgr.RemoveCommandGroup2(GroupId, true);
            _group = null;
        }

        public int EnableAlways() { return 1; }

        /// <summary>Repository root (holds gearset.py): registry, environment, or walking up from the dll.</summary>
        public static string FindRoot()
        {
            var candidates = new List<string>();
            try { using (var k = Registry.CurrentUser.OpenSubKey(GearSetForm.RegistryKey)) if (k != null) candidates.Add(k.GetValue("Root") as string); } catch { }
            candidates.Add(Environment.GetEnvironmentVariable("GEARSET_ROOT"));
            string dir = Path.GetDirectoryName(typeof(GearSetAddin).Assembly.Location);
            for (int i = 0; i < 6 && dir != null; i++) { candidates.Add(dir); dir = Path.GetDirectoryName(dir); }
            foreach (string c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "gearset.py"))) return c;
            return null;
        }

        /// <summary>CommandManager button: dialog, then build.</summary>
        public void CreateGearSet()
        {
            try
            {
                GearModel model; string folder; bool mates;
                using (var form = new GearSetForm(FindRoot()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    model = form.Result; folder = form.OutputFolder; mates = form.AddMates;
                }
                string message;
                bool ok = BuildNow(model, folder, mates, out message);
                Msg(message, !ok);
            }
            catch (Exception ex) { Msg("Gear Set error:\n" + ex, true); }
        }

        /// <summary>Headless build (same pipeline as the dialog) for automation and tests.</summary>
        public bool BuildNow(GearModel model, string folder, bool addMates, out string message)
        {
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                var builder = new GearBuilder(_sw);
                builder.Progress = s => Log(s);
                BuildResult r = builder.Build(model, folder, true, addMates);
                foreach (string line in r.MateReport) Log("mate: " + line);
                try { using (var k = Registry.CurrentUser.CreateSubKey(GearSetForm.RegistryKey)) if (k != null) k.SetValue("LastFolder", r.Folder); } catch { }
                string mateText = !addMates ? "No mates added (unticked)." :
                    r.MatesAdded == r.MatesWanted ? "Mates: all " + r.MatesAdded + " added - drag the sun to animate." + (r.MotionStudy != null ? "\n" + r.MotionStudy : "")
                                                  : "Mates: " + r.MatesAdded + " of " + r.MatesWanted + " added:\n" + string.Join("\n", r.MateReport.ToArray());
                message = "Gear set " + model.Name + " built.\n\n" + string.Join("\n", r.Files.ConvertAll(p => Path.GetFileName(p)).ToArray())
                        + "\n\nin " + r.Folder + "\n\n" + mateText + "\n\n" + model.DimensionsText();
                return true;
            }
            catch (Exception ex)
            {
                Log("build failed: " + ex);
                message = "Building the gear set failed:\n" + ex.Message + "\n\nSee %TEMP%\\GearSetAddin.log";
                return false;
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        public void OpenOutputFolder()
        {
            try
            {
                string last = null;
                using (var k = Registry.CurrentUser.OpenSubKey(GearSetForm.RegistryKey)) if (k != null) last = k.GetValue("LastFolder") as string;
                if (string.IsNullOrEmpty(last) || !Directory.Exists(last)) { Msg("No gear set has been built yet.", true); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + last + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Msg("Could not open the folder: " + ex.Message, true); }
        }

        private void Msg(string text, bool warning)
        {
            _sw.SendMsgToUser2(text, (int)(warning ? swMessageBoxIcon_e.swMbWarning : swMessageBoxIcon_e.swMbInformation), (int)swMessageBoxBtn_e.swMbOk);
        }

        // ------------------------------------------------------------------ COM registration
        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string key = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hklm.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + key))
            {
                k.SetValue(null, 0, RegistryValueKind.DWord);
                k.SetValue("Title", Title);
                k.SetValue("Description", Description);
            }
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                using (var k = hkcu.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + key))
                    k.SetValue(null, 1, RegistryValueKind.DWord);
                // per-user add-in entry as well (SOLIDWORKS reads HKCU\Software\SolidWorks\Addins too)
                using (var k = hkcu.CreateSubKey(@"Software\SolidWorks\Addins\" + key))
                {
                    k.SetValue(null, 0, RegistryValueKind.DWord);
                    k.SetValue("Title", Title);
                    k.SetValue("Description", Description);
                }
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            string key = "{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                hklm.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\Addins\" + key, false);
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                hkcu.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + key, false);
                hkcu.DeleteSubKeyTree(@"Software\SolidWorks\Addins\" + key, false);
            }
        }
    }
}
