namespace Spice86.Core.Emulator.OperatingSystem;

using Serilog.Events;

using Spice86.Core.CLI;
using Spice86.Core.Emulator.CPU;
using Spice86.Core.Emulator.Devices.Video;
using Spice86.Core.Emulator.Function;
using Spice86.Core.Emulator.InterruptHandlers.Dos;
using Spice86.Core.Emulator.InterruptHandlers.Dos.Ems;
using Spice86.Core.Emulator.InterruptHandlers.Input.Keyboard;
using Spice86.Core.Emulator.Memory;
using Spice86.Core.Emulator.OperatingSystem.Devices;
using Spice86.Core.Emulator.OperatingSystem.Enums;
using Spice86.Core.Emulator.OperatingSystem.Structures;
using Spice86.Shared.Emulator.Memory;
using Spice86.Shared.Interfaces;
using Spice86.Shared.Utils;

using System.Text;

/// <summary>
/// Represents the DOS kernel.
/// </summary>
public sealed class Dos {
    //in DOSBox, this is the 'DOS_INFOBLOCK_SEG'
    private const int DosSysVarSegment = 0x80;
    private readonly BiosDataArea _biosDataArea;
    private readonly IVgaFunctionality _vgaFunctionality;
    private readonly BiosKeyboardBuffer _biosKeyboardBuffer;
    private readonly IMemory _memory;
    private readonly ILoggerService _loggerService;

    /// <summary>
    /// Gets the INT 20h DOS services.
    /// </summary>
    public DosInt20Handler DosInt20Handler { get; }

    /// <summary>
    /// Gets the INT 21h DOS services.
    /// </summary>
    public DosInt21Handler DosInt21Handler { get; }

    /// <summary>
    /// Gets the INT 2Fh DOS services.
    /// </summary>
    public DosInt2fHandler DosInt2FHandler { get; }

    /// <summary>
    /// Gets the INT 25H DOS Disk services.
    /// </summary>
    public DosDiskInt25Handler DosInt25Handler { get; }

    /// <summary>
    /// Gets the INT26H DOS Disk services stub.
    /// </summary>
    public DosDiskInt26Handler DosInt26Handler { get; }

    /// <summary>
    /// Gets the INT 28h DOS services.
    /// </summary>
    public DosInt28Handler DosInt28Handler { get; }

    /// <summary>
    /// The class that handles DOS drives, as a sorted dictionnary.
    /// </summary>
    public DosDriveManager DosDriveManager { get; }

    /// <summary>
    /// Gets the list of virtual devices.
    /// </summary>
    public readonly List<IVirtualDevice> Devices = new();

    /// <summary>
    /// Gets or sets the current clock device.
    /// </summary>
    public CharacterDevice CurrentClockDevice { get; set; } = null!;

    /// <summary>
    /// Gets or sets the current console device.
    /// </summary>
    public CharacterDevice CurrentConsoleDevice { get; set; } = null!;

    /// <summary>
    /// Gets the DOS memory manager.
    /// </summary>
    public DosMemoryManager MemoryManager { get; }

    /// <summary>
    /// Gets the DOS file manager.
    /// </summary>
    public DosFileManager FileManager { get; }

    public DosProcessManager ProcessManager { get; }

    /// <summary>
    /// Gets the global DOS region settings structure.
    /// </summary>
    public CountryInfo CountryInfo { get; }

    /// <summary>
    /// The movable data transfer area for DOS applications.
    /// </summary>
    public DosSwappableDataArea DosSwappableDataArea { get; }

    /// <summary>
    /// The DOS System information. Read by DOSINFO.
    /// </summary>
    /// <remarks>
    /// AKA the 'List of lists'
    /// </remarks>
    public DosSysVars DosSysVars { get; }

    /// <summary>
    /// The EMS device driver.
    /// </summary>
    public ExpandedMemoryManager? Ems { get; private set; }

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="configuration">An object that describes what to run and how.</param>
    /// <param name="memory">The emulator memory.</param>
    /// <param name="functionHandlerProvider">Provides current call flow handler to peek call stack.</param>
    /// <param name="stack">The CPU stack.</param>
    /// <param name="state">The CPU state.</param>
    /// <param name="biosKeyboardBuffer">The BIOS keyboard buffer structure in emulated memory.</param>
    /// <param name="keyboardInt16Handler">The BIOS interrupt handler that writes/reads the BIOS Keyboard Buffer.</param>
    /// <param name="biosDataArea">The memory mapped BIOS values and settings.</param>
    /// <param name="vgaFunctionality">The high-level VGA functions.</param>
    /// <param name="envVars">The DOS environment variables.</param>
    /// <param name="clock"></param>
    /// <param name="loggerService">The logger service implementation.</param>
    public Dos(Configuration configuration, IMemory memory,
        IFunctionHandlerProvider functionHandlerProvider, Stack stack, State state,
        BiosKeyboardBuffer biosKeyboardBuffer, KeyboardInt16Handler keyboardInt16Handler,
        BiosDataArea biosDataArea, IVgaFunctionality vgaFunctionality,
        IDictionary<string, string> envVars, Clock clock, ILoggerService loggerService) {
        _loggerService = loggerService;
        _biosKeyboardBuffer = biosKeyboardBuffer;
        _memory = memory;
        _biosDataArea = biosDataArea;
        _vgaFunctionality = vgaFunctionality;

        DosDriveManager = new(_loggerService, configuration.CDrive, configuration.Exe);

        VirtualFileBase[] dosDevices = AddDefaultDevices(state, keyboardInt16Handler);
        DosSysVars = new DosSysVars((NullDevice)dosDevices[0], memory,
            MemoryUtils.ToPhysicalAddress(DosSysVarSegment, 0x0));

        DosSysVars.ConsoleDeviceHeaderPointer = ((IVirtualDevice)dosDevices[1]).Header.BaseAddress;

        DosSwappableDataArea = new(_memory,
            MemoryUtils.ToPhysicalAddress(0xb2, 0));

        DosStringDecoder dosStringDecoder = new(memory, state);

        CountryInfo = new();
        FileManager = new DosFileManager(_memory, dosStringDecoder, DosDriveManager,
            _loggerService, this.Devices);
        ProcessManager = new(configuration, memory, state, FileManager, DosDriveManager, envVars, loggerService);
        MemoryManager = new DosMemoryManager(_memory, loggerService, ProcessManager.PspSegment, DosProcessManager.LastFreeSegment);
        DosSysVars.FirstMCB = ProcessManager.PspSegment;
        DosInt20Handler = new DosInt20Handler(_memory, functionHandlerProvider, stack, state, _loggerService);
        DosInt21Handler = new DosInt21Handler(_memory, functionHandlerProvider, stack, state,
            keyboardInt16Handler, CountryInfo, dosStringDecoder,
            MemoryManager, FileManager, DosDriveManager, clock, _loggerService);
        DosInt2FHandler = new DosInt2fHandler(_memory, functionHandlerProvider, stack, state, _loggerService);
        DosInt25Handler = new DosDiskInt25Handler(_memory, DosDriveManager, functionHandlerProvider, stack, state, _loggerService);
        DosInt26Handler = new DosDiskInt26Handler(_memory, DosDriveManager, functionHandlerProvider, stack, state, _loggerService);
        DosInt28Handler = new DosInt28Handler(_memory, functionHandlerProvider, stack, state, _loggerService);

        if (configuration.InitializeDOS is false) {
            return;
        }
        if (_loggerService.IsEnabled(LogEventLevel.Verbose)) {
            _loggerService.Verbose("Initializing DOS");
        }
        OpenDefaultFileHandles(dosDevices);
        if (configuration.Ems) {
            Ems = new(_memory, functionHandlerProvider, stack, state, _loggerService);
            AddDevice(Ems, ExpandedMemoryManager.DosDeviceSegment, 0);
        }
    }

    private void OpenDefaultFileHandles(VirtualFileBase[] fileDevices) {
        foreach (VirtualFileBase fileDevice in fileDevices) {
            FileManager.OpenDevice(fileDevice);
        }
    }

    private uint GetDefaultNewDeviceBaseAddress()
        => new SegmentedAddress(MemoryMap.DeviceDriverSegment, (ushort)(Devices.Count * DosDeviceHeader.HeaderLength)).Linear;

    private VirtualFileBase[] AddDefaultDevices(State state, KeyboardInt16Handler keyboardInt16Handler) {
        var nulDevice = new NullDevice(_loggerService, _memory, GetDefaultNewDeviceBaseAddress());
        AddDevice(nulDevice);
        var consoleDevice = new ConsoleDevice(_memory, GetDefaultNewDeviceBaseAddress(),
            _loggerService, state,
            _biosDataArea, keyboardInt16Handler, _vgaFunctionality,
            _biosKeyboardBuffer);
        AddDevice(consoleDevice);
        var printerDevice = new PrinterDevice(_loggerService, _memory, GetDefaultNewDeviceBaseAddress());
        AddDevice(printerDevice);
        var auxDevice = new AuxDevice(_loggerService, _memory, GetDefaultNewDeviceBaseAddress());
        AddDevice(auxDevice);
        for (int i = 0; i < DosDriveManager.Count; i++) {
            AddDevice(new BlockDevice(_memory, GetDefaultNewDeviceBaseAddress(),
                DeviceAttributes.FatDevice, 1));
        }
        return [nulDevice, consoleDevice, printerDevice];
    }

    /// <summary>
    /// Add a device to memory so that the information can be read by both DOS and programs.
    /// </summary>
    /// <param name="device">The DOS Device driver to add.</param>
    /// <param name="segment">The segment part of the segmented address for the DOS device header.</param>
    /// <param name="offset">The offset part of the segmented address for the DOS device header.</param>
    private void AddDevice(IVirtualDevice device, ushort? segment = null, ushort? offset = null) {
        DosDeviceHeader header = device.Header;
        // Store the location of the header
        segment ??= MemoryMap.DeviceDriverSegment;
        offset ??= (ushort)(Devices.Count * DosDeviceHeader.HeaderLength);
        // Write the DOS device driver header to memory
        ushort index = (ushort)(offset.Value + 10); //10 bytes in our DosDeviceHeader structure.
        if (header.Attributes.HasFlag(DeviceAttributes.Character)) {
            _memory.LoadData(MemoryUtils.ToPhysicalAddress(segment.Value, index),
                Encoding.ASCII.GetBytes( $"{device.Name,-8}"));
        } else if(device is BlockDevice blockDevice) {
            _memory.UInt8[segment.Value, index] = blockDevice.UnitCount;
            index++;
            _memory.LoadData(MemoryUtils.ToPhysicalAddress(segment.Value, index),
                Encoding.ASCII.GetBytes($"{blockDevice.Signature, -7}"));
        }

        // Make the previous device point to this one
        if (Devices.Count > 0) {
            IVirtualDevice previousDevice = Devices[^1];
            _memory.SegmentedAddress[previousDevice.Header.BaseAddress] =
                new SegmentedAddress(segment.Value, offset.Value);
        }

        // Handle changing of current input, output or clock devices.
        if (header.Attributes.HasFlag(DeviceAttributes.CurrentStdin) ||
            header.Attributes.HasFlag(DeviceAttributes.CurrentStdout)) {
            CurrentConsoleDevice = (CharacterDevice)device;
        }
        if (header.Attributes.HasFlag(DeviceAttributes.CurrentClock)) {
            CurrentClockDevice = (CharacterDevice)device;
        }
        Devices.Add(device);
    }
}