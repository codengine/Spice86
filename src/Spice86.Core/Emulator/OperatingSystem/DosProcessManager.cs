﻿namespace Spice86.Core.Emulator.OperatingSystem;

using Serilog.Events;

using Spice86.Core.CLI;
using Spice86.Core.Emulator.CPU;
using Spice86.Core.Emulator.LoadableFile.Dos;
using Spice86.Core.Emulator.Memory;
using Spice86.Core.Emulator.Memory.ReaderWriter;
using Spice86.Core.Emulator.OperatingSystem.Structures;
using Spice86.Shared.Emulator.Errors;
using Spice86.Shared.Emulator.Memory;
using Spice86.Shared.Interfaces;
using Spice86.Shared.Utils;

using System.Text;

public class DosProcessManager : DosFileLoader {
    private const ushort ComOffset = 0x100;
    private readonly ushort _startSegment;
    private readonly ushort _pspSegment;
    private readonly DosFileManager _fileManager;
    private readonly DosDriveManager _driveManager;

    /// <summary>
    /// The master environment block that all DOS PSPs inherit.
    /// </summary>
    /// <remarks>
    /// Not stored in emulated memory, so no one can modify it.
    /// </remarks>
    private readonly EnvironmentVariables _environmentVariables;

    public const ushort LastFreeSegment = MemoryMap.GraphicVideoMemorySegment - 1;

    public ushort PspSegment => _pspSegment;

    public DosProcessManager(Configuration configuration, IMemory memory,
        State state, DosFileManager dosFileManager, DosDriveManager dosDriveManager,
        IDictionary<string, string> envVars, ILoggerService loggerService)
        : base(memory, state, loggerService) {
        _fileManager = dosFileManager;
        _driveManager = dosDriveManager;
        _environmentVariables = new();
        _startSegment = configuration.ProgramEntryPointSegment;
        _pspSegment = (ushort)(_startSegment - 0x10);

        envVars.Add("PATH", $"{_driveManager.CurrentDrive.DosVolume}{DosPathResolver.DirectorySeparatorChar}");

        foreach (KeyValuePair<string, string> envVar in envVars) {
            _environmentVariables.Add(envVar.Key, envVar.Value);
        }
    }


    /// <summary>
    /// Converts the specified command-line arguments string into the format used by DOS.
    /// </summary>
    /// <param name="arguments">The command-line arguments string.</param>
    /// <returns>The command-line arguments in the format used by DOS.</returns>
    private static byte[] ArgumentsToDosBytes(string? arguments) {
        byte[] res = new byte[128];
        string correctLengthArguments = "";
        if (string.IsNullOrWhiteSpace(arguments) == false) {
            // Cut strings longer than 127 characters.
            correctLengthArguments = arguments.Length > 127 ? arguments[..127] : arguments;
        }

        // Set the command line size.
        res[0] = (byte)correctLengthArguments.Length;

        byte[] argumentsBytes = Encoding.ASCII.GetBytes(correctLengthArguments);

        // Copy the actual characters.
        int index = 0;
        for (; index < correctLengthArguments.Length; index++) {
            res[index + 1] = argumentsBytes[index];
        }

        res[index + 1] = 0x0D; // Carriage return.
        int endIndex = index + 1;
        return res[0..endIndex];
    }

    public override byte[] LoadFile(string file, string? arguments) {
        uint pspAddress = MemoryUtils.ToPhysicalAddress(PspSegment, 0);

        // Use DosProgramSegmentPrefix abstraction
        var psp = new DosProgramSegmentPrefix(_memory, pspAddress);

        // Set the PSP's first 2 bytes to INT 20h.
        psp.Exit[0] = 0xCD;
        psp.Exit[1] = 0x20;

        psp.NextSegment = LastFreeSegment;

        // Load the command-line arguments into the PSP's command tail.
        byte[] commandLineBytes = ArgumentsToDosBytes(arguments);
        byte length = commandLineBytes[0];
        string asciiCommandLine = Encoding.ASCII.GetString(commandLineBytes, 1, length);
        psp.DosCommandTail.Length = (byte)(asciiCommandLine.Length + 1);
        psp.DosCommandTail.Command = asciiCommandLine;

        byte[] environmentBlock = CreateEnvironmentBlock(file);

        // In the PSP, the Environment Block Segment field (defined at offset 0x2C) is a word, and is a pointer.
        int envBlockPointer = PspSegment + 1;
        SegmentedAddress envBlockSegmentAddress = new SegmentedAddress((ushort)envBlockPointer, 0);

        // Copy the environment block to memory in a separated segment.
        _memory.LoadData(MemoryUtils.ToPhysicalAddress(envBlockSegmentAddress.Segment,
            envBlockSegmentAddress.Offset), environmentBlock);

        // Point the PSP's environment segment to the environment block.
        psp.EnvironmentTableSegment = envBlockSegmentAddress.Segment;

        // Set the disk transfer area address to the command-line offset in the PSP.
        _fileManager.SetDiskTransferAreaAddress(PspSegment, DosCommandTail.OffsetInPspSegment);

        return Path.GetExtension(file).ToUpperInvariant() switch {
            ".COM" => LoadComFile(file),
            ".EXE" => LoadExeFile(file, PspSegment),
            _ => throw new UnrecoverableException($"Unsupported file type for DOS: {file}"),
        };
    }

    /// <summary>
    /// Creates a DOS environment block from the current environment variables.
    /// </summary>
    /// <param name="programPath">The path to the program being executed.</param>
    /// <returns>A byte array containing the DOS environment block.</returns>
    private byte[] CreateEnvironmentBlock(string programPath) {
        using MemoryStream ms = new();
    
        // Add each environment variable as NAME=VALUE followed by a null terminator
        foreach (KeyValuePair<string, string> envVar in _environmentVariables) {
            string envString = $"{envVar.Key}={envVar.Value}";
            byte[] envBytes = Encoding.ASCII.GetBytes(envString);
            ms.Write(envBytes, 0, envBytes.Length);
            ms.WriteByte(0); // Null terminator for this variable
        }
    
        // Add final null byte to mark end of environment block
        ms.WriteByte(0);
    
        // Write a word with value 1 after the environment variables
        // This is required by DOS
        ms.WriteByte(1);
        ms.WriteByte(0);
    
        // Get the DOS path for the program (not the host path)
        string dosPath = _fileManager.GetDosProgramPath(programPath);
    
        // Write the DOS path to the environment block
        byte[] programPathBytes = Encoding.ASCII.GetBytes(dosPath);
        ms.Write(programPathBytes, 0, programPathBytes.Length);
        ms.WriteByte(0); // Null terminator for program path
    
        return ms.ToArray();
    }

    

    private byte[] LoadComFile(string file) {
        byte[] com = ReadFile(file);
        uint physicalStartAddress = MemoryUtils.ToPhysicalAddress(_startSegment, ComOffset);
        _memory.LoadData(physicalStartAddress, com);

        // Make DS and ES point to the PSP
        _state.DS = _startSegment;
        _state.ES = _startSegment;
        SetEntryPoint(_startSegment, ComOffset);
        _state.InterruptFlag = true;
        return com;
    }

    private byte[] LoadExeFile(string file, ushort pspSegment) {
        byte[] exe = ReadFile(file);
        if (_loggerService.IsEnabled(LogEventLevel.Debug)) {
            _loggerService.Debug("Exe size: {ExeSize}", exe.Length);
        }
        DosExeFile exeFile = new DosExeFile(new ByteArrayReaderWriter(exe));
        if (!exeFile.IsValid) {
            if (_loggerService.IsEnabled(LogEventLevel.Error)) {
                _loggerService.Error("Invalid EXE file {File}", file);
            }
            throw new UnrecoverableException($"Invalid EXE file {file}");
        }
        if (_loggerService.IsEnabled(LogEventLevel.Verbose)) {
            _loggerService.Verbose("Read header: {ReadHeader}", exeFile);
        }

        LoadExeFileInMemoryAndApplyRelocations(exeFile, _startSegment);
        SetupCpuForExe(exeFile, _startSegment, pspSegment);
        if (_loggerService.IsEnabled(LogEventLevel.Debug)) {
            _loggerService.Debug("Initial CPU State: {CpuState}", _state);
        }
        return exe;
    }

    /// <summary>
    /// Loads the program image and applies any necessary relocations to it.
    /// </summary>
    /// <param name="exeFile">The EXE file to load.</param>
    /// <param name="startSegment">The starting segment for the program.</param>
    private void LoadExeFileInMemoryAndApplyRelocations(DosExeFile exeFile, ushort startSegment) {
        uint physicalStartAddress = MemoryUtils.ToPhysicalAddress(startSegment, 0);
        _memory.LoadData(physicalStartAddress, exeFile.ProgramImage, (int)exeFile.ProgramSize);
        foreach (SegmentedAddress address in exeFile.RelocationTable) {
            // Read value from memory, add the start segment offset and write back
            uint addressToEdit = MemoryUtils.ToPhysicalAddress(address.Segment, address.Offset)
                + physicalStartAddress;
            _memory.UInt16[addressToEdit] += startSegment;
        }
    }

    /// <summary>
    /// Sets up the CPU to execute the loaded program.
    /// </summary>
    /// <param name="exeFile">The EXE file that was loaded.</param>
    /// <param name="startSegment">The starting segment address of the program.</param>
    /// <param name="pspSegment">The segment address of the program's PSP (Program Segment Prefix).</param>
    private void SetupCpuForExe(DosExeFile exeFile, ushort startSegment, ushort pspSegment) {
        // MS-DOS uses the values in the file header to set the SP and SS registers and
        // adjusts the initial value of the SS register by adding the start-segment
        // address to it.
        _state.SS = (ushort)(exeFile.InitSS + startSegment);
        _state.SP = exeFile.InitSP;

        // Make DS and ES point to the PSP
        _state.DS = pspSegment;
        _state.ES = pspSegment;

        _state.InterruptFlag = true;

        // Finally, MS-DOS reads the initial CS and IP values from the program's file
        // header, adjusts the CS register value by adding the start-segment address to
        // it, and transfers control to the program at the adjusted address.
        SetEntryPoint((ushort)(exeFile.InitCS + startSegment), exeFile.InitIP);
    }
}