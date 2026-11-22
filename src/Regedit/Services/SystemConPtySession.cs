// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

#if REGEDIT_SYSTEM_CONPTY
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Terminal.Wpf;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;

namespace JPSoftworks.Regedit.Services;

internal sealed class SystemConPtySession : ITerminalConnection
{
    private readonly Lock _consoleLock = new();
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private PseudoConsoleHandle? _console;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    internal async Task RunAsync(string commandLine, int columns, int rows, Func<bool> connect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new AnonymousPipeServerStream(PipeDirection.Out);
        using var output = new AnonymousPipeServerStream(PipeDirection.In);
        using var console = CreateConsole(input, output, columns, rows);
        Task? outputPump = null;
        Task inputPump = Task.CompletedTask;
        try
        {
            using var process = StartProcess(commandLine, console);
            try
            {
                input.DisposeLocalCopyOfClientHandle();
                output.DisposeLocalCopyOfClientHandle();
                lock (this._consoleLock)
                {
                    this._console = console;
                }

                var connected = connect();
                outputPump = Task.Run(() => this.ReadOutput(output, connected));
                if (connected)
                {
                    inputPump = Task.Run(() => this.SendInputAsync(input, cancellationToken));
                    await await Task.WhenAny(process.WaitForExitAsync(cancellationToken), outputPump, inputPump);
                }
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // PowerShell can exit while the session is closing.
                }
            }
        }
        finally
        {
            this.Close();
            lock (this._consoleLock)
            {
                this._console = null;
            }

            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            if (outputPump is null)
            {
                output.Dispose();
            }

            // Older Windows builds can block here until the output pipe is drained.
            console.Dispose();
            await Task.WhenAll(inputPump, outputPump ?? Task.CompletedTask);
        }
    }

    internal void Win32DirectInputMode(bool enabled) =>
        this.TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(enabled ? "\x1b[?9001h" : "\x1b[?9001l"));

    // RunAsync starts the session. The renderer calls Start when it subscribes to output.
    public void Start() { }

    public void WriteInput(string data) => this._input.Writer.TryWrite(data);

    public void Resize(uint rows, uint columns)
    {
        lock (this._consoleLock)
        {
            if (this._console is { } console)
            {
                PInvoke.ResizePseudoConsole(new HPCON(console.DangerousGetHandle()), GetSize(columns, rows));
            }
        }
    }

    public void Close() => this._input.Writer.TryComplete();

    private void ReadOutput(Stream output, bool connected)
    {
        using var reader = new StreamReader(output, Encoding.UTF8);
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer)) > 0)
        {
            if (connected)
            {
                this.TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(new string(buffer, 0, count)));
            }
        }
    }

    private async Task SendInputAsync(PipeStream input, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var data in this._input.Reader.ReadAllAsync(cancellationToken))
            {
                input.Write(Encoding.UTF8.GetBytes(data));
            }
        }
        catch (IOException) when (!input.IsConnected || cancellationToken.IsCancellationRequested)
        {
            // The console may close the pipe during a write.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static COORD GetSize(uint columns, uint rows) => new()
    {
        X = (short)Math.Clamp(columns, 1, short.MaxValue),
        Y = (short)Math.Clamp(rows, 1, short.MaxValue)
    };

    private static unsafe PseudoConsoleHandle CreateConsole(
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, int columns, int rows)
    {
        HPCON console;
        var result = PInvoke.CreatePseudoConsole(
            GetSize((uint)columns, (uint)rows),
            new HANDLE(input.ClientSafePipeHandle.DangerousGetHandle()),
            new HANDLE(output.ClientSafePipeHandle.DangerousGetHandle()),
            0,
            &console);
        Marshal.ThrowExceptionForHR(result.Value);
        return new PseudoConsoleHandle(console);
    }

    private static unsafe Process StartProcess(string commandLine, PseudoConsoleHandle console)
    {
        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        if (size == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var attributes = new byte[checked((int)size)];
        var command = (commandLine + '\0').ToCharArray();
        fixed (byte* attributeBuffer = attributes)
        fixed (char* commandBuffer = command)
        {
            var startup = new STARTUPINFOEXW
            {
                StartupInfo =
                {
                    cb = (uint)sizeof(STARTUPINFOEXW),
                    // Null standard handles let ConPTY supply them even when Regedit was launched with redirection.
                    dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES
                },
                lpAttributeList = new LPPROC_THREAD_ATTRIBUTE_LIST(attributeBuffer)
            };
            if (!PInvoke.InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                if (!PInvoke.UpdateProcThreadAttribute(startup.lpAttributeList, 0,
                        PInvoke.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, (void*)console.DangerousGetHandle(),
                        (nuint)IntPtr.Size, null, null))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                PROCESS_INFORMATION child;
                if (!PInvoke.CreateProcess(null, commandBuffer, null, null, false,
                        PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT, null, null, &startup.StartupInfo, &child))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                using var processHandle = new SafeProcessHandle((nint)child.hProcess.Value, ownsHandle: true);
                using var threadHandle = new SafeFileHandle((nint)child.hThread.Value, ownsHandle: true);
                Process? process = null;
                try
                {
                    process = Process.GetProcessById((int)child.dwProcessId);
                    _ = process.SafeHandle;
                    return process;
                }
                catch
                {
                    process?.Dispose();
                    PInvoke.TerminateProcess(child.hProcess, 1);
                    throw;
                }
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(startup.lpAttributeList);
            }
        }
    }

    private sealed class PseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal PseudoConsoleHandle(HPCON console) : base(ownsHandle: true) => this.SetHandle(console.Value);

        protected override bool ReleaseHandle()
        {
            PInvoke.ClosePseudoConsole(new HPCON(this.handle));
            return true;
        }
    }
}
#endif
