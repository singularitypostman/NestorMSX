﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Konamiman.NestorMSX.BuiltInPlugins.MemoryTypes;
using Konamiman.NestorMSX.Hardware;
using Konamiman.NestorMSX.Menus;
using Konamiman.NestorMSX.Misc;
using Konamiman.Z80dotNet;
using System.Linq;
using System.Windows.Forms;

namespace Konamiman.NestorMSX.Plugins
{
    [NestorMSXPlugin("Nextor")]
    public class NextorPlugin : DiskImageBasedStoragePlugin
    {
        private Ascii8Rom kernelMemory;
        private IDictionary<ushort, Action> kernelRoutines;

        public NextorPlugin(PluginContext context, IDictionary<string, object> pluginConfig)
            : base(context, pluginConfig)
        {
            kernelRoutines = new Dictionary<ushort, Action>
            {
                { 0x4133, DRV_VERSION },
                { 0x4136, DRV_INIT },
                { 0x4160, DEV_RW },
                { 0x4163, DEV_INFO },
                { 0x4166, DEV_STATUS },
                { 0x4169, LUN_INFO  }
            };
        }

        protected override int MaxNumberOfDevices { get { return 7; } }

        protected override IMemory GetMemory(byte[] kernelFileContents)
        {
            const int driverOffset = 112 * 1024;

            kernelFileContents[driverOffset + 0x010E] = 1; //Device-based driver
            Array.Copy(PaddedArrayFromString("NestorMSX Nextor plugin", 32), 0, kernelFileContents, driverOffset + 0x0110, 32);

            kernelMemory = new Ascii8Rom(kernelFileContents);
            return kernelMemory;
        }

        protected override string PluginDisplayName { get { return "Nextor"; } }

        protected override void BeforeZ80InstructionFetch(ushort instructionAddress)
        {
            if (memory.GetCurrentSlot(1) != slotNumber)
                return;

            if (kernelMemory.CurrentBlockInBank(1) != 14)
                return;

            if (kernelRoutines.ContainsKey(z80.Registers.PC))
            {
                var routine = kernelRoutines[z80.Registers.PC];
                routine();
                z80.ExecuteRet();
            }
        }

        private void DRV_VERSION()
        {
            z80.Registers.A = 1;
            z80.Registers.BC = 0;
        }

        private void DRV_INIT()
        {
            if (z80.Registers.A == 0)
            {
                z80.Registers.HL = 0;
                z80.Registers.CF = 0;
            }
        }

        private void DEV_RW()
        {
            var deviceIndex = z80.Registers.A;
            var numberOfSectors = z80.Registers.B;
            var logicalUnit = z80.Registers.C;
            var memoryAddress = z80.Registers.HL.ToUShort();
            var sectorAddress = z80.Registers.DE.ToUShort();

            if (!IsValidDevice(deviceIndex) || logicalUnit != 1)
            {
                z80.Registers.A = _IDEVL;
                z80.Registers.B = 0;
                return;
            }

            var sectorNumber =
                memory[sectorAddress]
                + 256 * memory[sectorAddress + 1]
                + 256 * 256 * memory[sectorAddress + 2]
                + 256 * 256 * 256 * memory[sectorAddress + 3];

            var file = imageFiles[deviceIndex - 1];
            if (sectorNumber > file.MaxSectorNumber)
            {
                z80.Registers.A = _RNF;
                z80.Registers.B = 0;
                return;
            }

            if (z80.Registers.CF && file.IsReadOnly)
            {
                z80.Registers.A = _WPROT;
                z80.Registers.B = 0;
                return;
            }

            file.Stream.Seek(sectorNumber * 512, SeekOrigin.Begin);

            try
            {
                if (z80.Registers.CF)
                    WriteSectors(sectorNumber, memoryAddress, numberOfSectors, file.Stream);
                else
                    ReadSectors(sectorNumber, memoryAddress, numberOfSectors, file.Stream);

                z80.Registers.B = numberOfSectors;
                z80.Registers.A = 0;
            }
            catch (Exception)
            {
                z80.Registers.B = 0;
                z80.Registers.A = _DISK;
            }
        }

        private void DEV_INFO()
        {
            var deviceIndex = z80.Registers.A;
            var infoBlockIndex = z80.Registers.B;
            var memoryAddress = z80.Registers.HL.ToUShort();

            if (!IsValidDevice(deviceIndex))
            {
                z80.Registers.A = 1; //Device not available
                return;
            }

            var file = imageFiles[deviceIndex - 1];
            string info = null;

            if (infoBlockIndex == 0)
            {
                memory[memoryAddress] = 1; //One logical unit
                memory[memoryAddress + 1] = 0; //Features
                z80.Registers.A = 0;
                return;
            }
            else if (infoBlockIndex == 1)
            {
                info = "Konamiman";
            }
            else if (infoBlockIndex == 2)
            {
                info = Path.GetFileName(file.FullPath);
            }
            else if (infoBlockIndex == 3)
            {
                info = "1";
            }
            else {
                z80.Registers.A = 1;
                return;
            }

            z80.Registers.A = 0;
            SetMemoryContents(memoryAddress, PaddedArrayFromString(info, 64));
        }

        private void DEV_STATUS()
        {
            var deviceIndex = z80.Registers.A;
            var logicalUnit = z80.Registers.B;

            if (!IsValidDevice(deviceIndex) || logicalUnit != 1)
                z80.Registers.A = 0; //Invalid device/LUN
            else if (imageFiles[deviceIndex - 1].HasChanged)
            {
                z80.Registers.A = 2; //Available and has changed
                imageFiles[deviceIndex - 1].HasChanged = false;
            }
            else
                z80.Registers.A = 1; //Available and has not changed
        }

        private void LUN_INFO()
        {
            var deviceIndex = z80.Registers.A;
            var logicalUnit = z80.Registers.B;
            var memoryAddress = z80.Registers.HL.ToUShort();

            if (!IsValidDevice(deviceIndex) || logicalUnit != 1)
            {
                z80.Registers.A = 1; //Invalid device/LUN
                return;
            }

            var file = imageFiles[deviceIndex - 1];
            var info = new byte[12];

            info[2] = 2; //sector size = 0x200
            info[7] = 1; //removable

            var numberOfSectors = BitConverter.GetBytes(file.MaxSectorNumber + 1);
            if (BitConverter.IsLittleEndian)
            {
                Array.Copy(numberOfSectors, 0, info, 3, 4);
            }
            else {
                info[0] = numberOfSectors[3];
                info[1] = numberOfSectors[2];
                info[2] = numberOfSectors[1];
                info[3] = numberOfSectors[0];
            }

            SetMemoryContents(memoryAddress, info);

            z80.Registers.A = 0;
        }
    }

    public abstract class DiskImageBasedStoragePlugin : IDisposable
    {
        protected const int _IDEVL = 0xB5;
        protected const int _WPROT = 0xF8;
        protected const int _RNF = 0xF9;
        protected const int _DISK = 0xFD;

        protected abstract int MaxNumberOfDevices { get; }

        private readonly string kernelFilePath;
        protected IZ80Processor z80 { get; }
        protected SlotNumber slotNumber { get; }
        protected IExternallyControlledSlotsSystem memory { get; }
        private string basePathForDiskImages;
        private OpenFileDialog openFileDialog;
        private Form hostForm;
        private Action<object, MenuEntry> setMenuEntry;
        private MenuEntry[] setFileMenuEntries;
        private MenuEntry[] removeFileMenuEntries;
        private MenuEntry removeFileMainMenuEntry;
        private MenuEntry separatorBeforeRemoveFileMenuEntry;
        private string[] filesListInConfig;
        private string[] filesListFromState;
        private string stateFileFullPath;

        protected ImageFileInfo[] imageFiles { get; }

        private byte[] kernelContents;

        protected class ImageFileInfo
        {
            public string FullPath { get; set; }
            public FileStream Stream { get; set; }
            public long MaxSectorNumber { get; set; }
            public bool IsReadOnly { get; set; }
            public bool HasChanged { get; set; }
        }

        protected abstract IMemory GetMemory(byte[] kernelFileContents);

        protected abstract string PluginDisplayName { get; }

        public DiskImageBasedStoragePlugin(PluginContext context, IDictionary<string, object> pluginConfig)
        {
           this.basePathForDiskImages =
                pluginConfig.GetValueOrDefault<string>("diskImagesDirectory", "").AsAbsolutePath();

            filesListInConfig = pluginConfig
                .GetValueOrDefault("diskImageFiles", new string[0])
                .Select(f => f.AsAbsolutePath(basePathForDiskImages))
                .ToArray();

            this.slotNumber = new SlotNumber(pluginConfig.GetValue<byte>("slotNumber"));

            var machineName = pluginConfig.GetValue<string>("NestorMSX.machineName");
            stateFileFullPath = $"{machineName}/{PluginDisplayName} in slot {slotNumber}.dat".AsAbsolutePath();
            if(File.Exists(stateFileFullPath))
                filesListFromState = File.ReadAllLines(stateFileFullPath, Encoding.UTF8).WithEmptiesAsNulls();
            else
                filesListFromState = filesListInConfig.WithMinimumSizeOf(MaxNumberOfDevices);

            imageFiles = new ImageFileInfo[MaxNumberOfDevices];

            this.setMenuEntry = context.SetMenuEntry;
            CreateMenu();

            this.openFileDialog = new OpenFileDialog();
            openFileDialog.Title = "Select disk image file";
            openFileDialog.InitialDirectory = basePathForDiskImages.Replace('/', Path.DirectorySeparatorChar);
            openFileDialog.Filter = "Disk image files|*.dsk;*.img|All files|*.*";
            this.hostForm = context.HostForm;

            this.kernelFilePath = pluginConfig.GetMachineFilePath(pluginConfig.GetValue<string>("kernelFile"));

            for (int i = 0; i < filesListFromState.Length; i++) {
                if(filesListFromState[i] == null) continue;
                var diskImageFilePath = filesListFromState[i].AsAbsolutePath(basePathForDiskImages);
                SetFile(diskImageFilePath, i + 1, Path.GetFileName(diskImageFilePath));
            }

            for (int i = 0; i < MaxNumberOfDevices; i++)
                removeFileMenuEntries[i].IsVisible = imageFiles[i] != null;

            this.z80 = context.Cpu;
            this.memory = context.SlotsSystem;
            
            z80.BeforeInstructionFetch += (sender, args) => BeforeZ80InstructionFetch(z80.Registers.PC);

            this.kernelContents = File.ReadAllBytes(kernelFilePath);
        }

        private void SetAllFilesFromConfig()
        {
            for(int i=1; i<=MaxNumberOfDevices; i++)
                RemoveDiskImageFile(i, false);

            for(int i = 0; i < filesListInConfig.Length; i++) {
                var diskImageFilePath = filesListInConfig[i].AsAbsolutePath(basePathForDiskImages);
                SetFile(diskImageFilePath, i + 1, Path.GetFileName(diskImageFilePath));
            }

            File.Delete(stateFileFullPath);
        }

        private void SetFile(string fullPath, int deviceIndex, string displayName)
        {
            var info = new ImageFileInfo();
            info.FullPath = fullPath;

            try {
                var fileAccess = new FileInfo(fullPath).IsReadOnly ? FileAccess.Read : FileAccess.ReadWrite;
                info.Stream = File.Open(fullPath, FileMode.Open, fileAccess);
                info.IsReadOnly = fileAccess == FileAccess.Read;
            } catch(Exception ex) {
                var message = $"Can't open disk image file:\r\n{fullPath}\r\n\r\n{ex.Message}";
                MessageBox.Show(message, $"NestorMSX - {PluginDisplayName} in slot {slotNumber}");
                return;
            }

            info.MaxSectorNumber = ((new FileInfo(fullPath)).Length) / 512 - 1;

            RemoveDiskImageFile(deviceIndex, false);
            imageFiles[deviceIndex - 1] = info;

            setFileMenuEntries[deviceIndex - 1].Title = $"{deviceIndex}: {Path.GetFileName(fullPath)}";
            removeFileMenuEntries[deviceIndex - 1].IsVisible = true;
            removeFileMainMenuEntry.IsVisible = true;
            separatorBeforeRemoveFileMenuEntry.IsVisible = true;
        }

        private void CreateMenu()
        {
            var menuEntries = Enumerable.Range(1, 7).Select(i =>
                new MenuEntry($"{i}: {(imageFiles[i-1] == null ? "(no file)" : Path.GetFileName(imageFiles[i-1].FullPath))}",
                () => AskAndSetFileForDevice(i)))
                .ToList();

            separatorBeforeRemoveFileMenuEntry = MenuEntry.CreateSeparator(isVisible: false);
            menuEntries.Add(separatorBeforeRemoveFileMenuEntry);

            removeFileMenuEntries = Enumerable.Range(1, 7).Select(i =>
                new MenuEntry(i.ToString(), () => RemoveDiskImageFile(i, true)) {IsVisible = false})
                .ToArray();

            removeFileMainMenuEntry = new MenuEntry("Remove image file", removeFileMenuEntries) { IsVisible = false };
            menuEntries.Add(removeFileMainMenuEntry);

            if(filesListInConfig.Length > 0) 
                menuEntries.Add(new MenuEntry("Set all from config", SetAllFilesFromConfig));

            var mainEntry = new MenuEntry($"{PluginDisplayName} in slot {slotNumber}", menuEntries);

            this.setFileMenuEntries = menuEntries.ToArray();
            setMenuEntry(this, mainEntry);
        }

        private void RemoveDiskImageFile(int deviceIndex, bool updateStateFile)
        {
            var fileIndex = deviceIndex - 1;

            if(imageFiles[fileIndex] != null) {
                try
                {
                    imageFiles[fileIndex].Stream.Dispose();
                }
                catch
                {
                }
                imageFiles[fileIndex] = null;
            }

            setFileMenuEntries[fileIndex].Title = $"{deviceIndex}: (no file)";
            removeFileMenuEntries[fileIndex].IsVisible = false;

            removeFileMainMenuEntry.IsVisible = removeFileMenuEntries.Any(m => m.IsVisible);
            separatorBeforeRemoveFileMenuEntry.IsVisible =
                removeFileMainMenuEntry.IsVisible ||
                filesListInConfig.Length > 0;

            if(updateStateFile)
                UpdateStateFile();
        }

        private void AskAndSetFileForDevice(int deviceIndex)
        {
            var dialogResult = openFileDialog.ShowDialog(hostForm);
            if(dialogResult != DialogResult.OK)
                return;

            var fileIndex = deviceIndex - 1;
            var hadPreviousFile = imageFiles[fileIndex] != null;
            SetFile(openFileDialog.FileName, deviceIndex, openFileDialog.SafeFileName);
            imageFiles[fileIndex].HasChanged = hadPreviousFile;

            UpdateStateFile();
        }

        private void UpdateStateFile()
        {
            try {
                File.WriteAllLines(stateFileFullPath, filesListFromState);
            }
            catch { }
        }

        protected byte[] PaddedArrayFromString(string theString, int totalLength)
        {
            if(theString.Length > totalLength)
                theString = theString.Remove(totalLength);
            return Encoding.ASCII.GetBytes(theString.PadRight(totalLength));
        }

        public IMemory GetMemory()
        {
            return GetMemory(kernelContents);
        }

        protected abstract void BeforeZ80InstructionFetch(ushort instructionAddress);
        
        protected bool IsValidDevice(byte deviceIndex)
        {
            return deviceIndex >= 1 && deviceIndex <= MaxNumberOfDevices && imageFiles[deviceIndex - 1] != null;
        }

        protected void ReadSectors(int sectorNumber, ushort memoryAddress, byte numberOfSectors, FileStream stream)
        {
            var data = new byte[numberOfSectors * 512];
            stream.Read(data, 0, data.Length);

            SetMemoryContents(memoryAddress, data);
        }

        protected void WriteSectors(int sectorNumber, ushort memoryAddress, byte numberOfSectors, FileStream stream)
        {
            var data = new byte[numberOfSectors * 512];

            for(var i = 0; i < data.Length; i++)
                data[i] = memory[memoryAddress + i];

            stream.Write(data, 0, data.Length);
            stream.Flush(true);
        }
        
        protected void SetMemoryContents(int memoryAddress, byte[] contents)
        {
            for(var i = 0; i < contents.Length; i++)
                memory[memoryAddress + i] = contents[i];
        }

        public void Dispose()
        {
            foreach(var file in imageFiles.Where(f => f!=null))
                file.Stream.Dispose();
        }
    }
}
