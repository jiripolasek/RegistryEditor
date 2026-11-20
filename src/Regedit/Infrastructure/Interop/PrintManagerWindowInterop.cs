// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Foundation;
using WinRT;
using WinRT.Interop;

namespace JPSoftworks.Regedit.Infrastructure.Interop;

internal static partial class PrintManagerWindowInterop
{
    // The desktop SDK helper casts an untyped result to IAsyncOperation<bool>, which fails under Native AOT.
    internal static async Task<bool> ShowAsync(nint window)
    {
        using var operation = OpenPrintUI(window);
        var handler = new CompletionHandler();
        unsafe
        {
            var callback = ComInterfaceMarshaller<ICompletionHandler>.ConvertToUnmanaged(handler);
            try
            {
                // IAsyncOperation<bool>.put_Completed follows the six IInspectable methods.
                var putCompleted = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(nint**)operation.ThisPtr)[6];
                ExceptionHelpers.ThrowExceptionForHR(putCompleted(operation.ThisPtr, callback));
            }
            finally
            {
                ComInterfaceMarshaller<ICompletionHandler>.Free(callback);
            }
        }

        if (await handler.Finished.Task == AsyncStatus.Canceled)
        {
            throw new OperationCanceledException();
        }

        return ABI.Windows.Foundation.IAsyncOperationMethods<bool, byte>.GetResults(operation);
    }

    private static unsafe ObjectReference<IUnknownVftbl> OpenPrintUI(nint window)
    {
        using var factory = ActivationFactory.Get(
            "Windows.Graphics.Printing.PrintManager", new Guid("c5435a42-8d43-4e7b-a68a-ef311e392087"));
        var iid = ABI.Windows.Foundation.IAsyncOperationMethods<bool>.IID;
        nint operation = 0;
        // IPrintManagerInterop.ShowPrintUIForWindowAsync follows GetForWindow in PrintManagerInterop.h.
        var showPrintUI = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory.ThisPtr)[7];
        ExceptionHelpers.ThrowExceptionForHR(showPrintUI(factory.ThisPtr, window, &iid, &operation));
        return ObjectReference<IUnknownVftbl>.Attach(ref operation, iid);
    }

    // The Windows.Foundation.AsyncOperationCompletedHandler<bool> delegate derives from IUnknown.
    [GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
    [Guid("c1d3d1a2-ae17-5a5f-b5a2-bdcc8844889a")]
    internal partial interface ICompletionHandler
    {
        void Invoke(nint operation, AsyncStatus status);
    }

    [GeneratedComClass]
    internal sealed partial class CompletionHandler : ICompletionHandler
    {
        internal TaskCompletionSource<AsyncStatus> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Invoke(nint operation, AsyncStatus status)
        {
            this.Finished.TrySetResult(status);
        }
    }
}
