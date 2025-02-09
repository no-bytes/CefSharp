// Copyright © 2018 The CefSharp Authors. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using System;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rect = CefSharp.Structs.Rect;

namespace CefSharp.Wpf.Rendering
{
    /// <summary>
    /// WritableBitmapRenderHandler - creates/updates an WritableBitmap
    /// Uses a MemoryMappedFile for double buffering when the size matches
    /// or creates a new WritableBitmap when required
    /// </summary>
    /// <seealso cref="CefSharp.Wpf.IRenderHandler" />
    public class WritableBitmapRenderHandler : AbstractRenderHandler
    {
        private readonly double dpiX;
        private readonly double dpiY;
        private readonly bool invalidateDirtyRect;

        /// <summary>
        /// Initializes a new instance of the <see cref="WritableBitmapRenderHandler"/> class.
        /// </summary>
        /// <param name="dpiX">The dpi x.</param>
        /// <param name="dpiY">The dpi y.</param>
        /// <param name="invalidateDirtyRect">if true then only the direct rectangle will be updated, otherwise the whole bitmap will be redrawn</param>
        /// <param name="dispatcherPriority">priority at which the bitmap will be updated on the UI thread</param>
        public WritableBitmapRenderHandler(double dpiX, double dpiY, bool invalidateDirtyRect = true, DispatcherPriority dispatcherPriority = DispatcherPriority.Render)
        {
            this.dpiX = dpiX;
            this.dpiY = dpiY;
            this.invalidateDirtyRect = invalidateDirtyRect;
            this.dispatcherPriority = dispatcherPriority;
        }

        /// <summary>
        /// When true if the Dirty Rect (rectangle that's to be updated)
        /// is smaller than the full width/height then only copy the Dirty Rect
        /// from the CEF native buffer to our own managed buffer.
        /// Set to true to improve performance when only a small portion of the screen is updated.
        /// Defaults to false currently.
        /// </summary>
        public bool CopyOnlyDirtyRect { get; set; }

        /// <inheritdoc/>
        protected override void CreateOrUpdateBitmap(bool isPopup, Rect dirtyRect, IntPtr buffer, int width, int height, Image image, ref Size currentSize, ref MemoryMappedFile mappedFile, ref MemoryMappedViewAccessor viewAccessor)
        {
            bool createNewBitmap = false;

            lock (lockObject)
            {
                int pixels = width * height;
                int numberOfBytes = pixels * BytesPerPixel;

                createNewBitmap = mappedFile == null || currentSize.Height != height || currentSize.Width != width;

                if (createNewBitmap)
                {
                    //If the MemoryMappedFile is smaller than we need then create a larger one
                    //If it's larger then we need then rather than going through the costly expense of
                    //allocating a new one we'll just use the old one and only access the number of bytes we require.
                    if (viewAccessor == null || viewAccessor.Capacity < numberOfBytes)
                    {
                        ReleaseMemoryMappedView(ref mappedFile, ref viewAccessor);

                        mappedFile = MemoryMappedFile.CreateNew(null, numberOfBytes, MemoryMappedFileAccess.ReadWrite);

                        viewAccessor = mappedFile.CreateViewAccessor();
                    }

                    currentSize.Height = height;
                    currentSize.Width = width;
                }

                if (CopyOnlyDirtyRect)
                {
                    // For full buffer update we just perform a simple copy
                    // otherwise only a portion will be updated.
                    if (width == dirtyRect.Width && height == dirtyRect.Height)
                    {
                        NativeMethodWrapper.MemoryCopy(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, numberOfBytes);
                    }
                    else
                    {
                        //TODO: We can probably perform some minor optimisations here.
                        //var numberOfBytesToCopy = dirtyRect.Width * BytesPerPixel;
                        //var safeMemoryMappedViewHandle = viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();

                        //for (int offset = width * dirtyRect.Y + dirtyRect.X; offset < (dirtyRect.Y + dirtyRect.Height) * width; offset += width)
                        //{
                        //    var b = offset * BytesPerPixel;
                        //    NativeMethodWrapper.MemoryCopy(safeMemoryMappedViewHandle + b, buffer + b, numberOfBytesToCopy);
                        //}

                        for (int offset = width * dirtyRect.Y + dirtyRect.X; offset < (dirtyRect.Y + dirtyRect.Height) * width; offset += width)
                        {
                            NativeMethodWrapper.MemoryCopy(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle() + offset * BytesPerPixel, buffer + offset * BytesPerPixel, dirtyRect.Width * BytesPerPixel);
                        }
                    }
                }
                else
                {
                    NativeMethodWrapper.MemoryCopy(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, numberOfBytes);
                }

                //Take a reference to the sourceBuffer that's used to update our WritableBitmap,
                //once we're on the UI thread we need to check if it's still valid
                var sourceBuffer = viewAccessor.SafeMemoryMappedViewHandle;

                image.Dispatcher.BeginInvoke((Action)(() =>
                {
                    lock (lockObject)
                    {
                        if (sourceBuffer.IsClosed || sourceBuffer.IsInvalid)
                        {
                            return;
                        }

                        var size = isPopup ? popupSize : viewSize;

                        //If OnPaint is called multiple times before
                        //our BeginInvoke call we check the size matches our most recent
                        //update, the buffer has already been overriden (frame is dropped effectively)
                        //so we ignore this call
                        //https://github.com/cefsharp/CefSharp/issues/3114
                        if (size.Width != width || size.Height != height)
                        {
                            return;
                        }

                        if (createNewBitmap)
                        {
                            if (image.Source != null)
                            {
                                image.Source = null;
                                GC.Collect(1);
                            }

                            image.Source = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormat, null);
                        }

                        var stride = width * BytesPerPixel;
                        var noOfBytes = stride * height;

                        var bitmap = (WriteableBitmap)image.Source;

                        //When agressively resizing the ChromiumWebBrowser sometimes
                        //we can end up with our buffer size not matching our bitmap size
                        //Just ignore these frames as the rendering should eventually catch up
                        //(CEF can generate multiple frames before WPF has performed a render cycle)
                        //https://github.com/cefsharp/CefSharp/issues/3474
                        if (width > bitmap.PixelWidth || height > bitmap.PixelHeight)
                        {
                            return;
                        }

                        var sourceBufferPtr = sourceBuffer.DangerousGetHandle();

                        // Issue https://github.com/cefsharp/CefSharp/issues/4426
                        if (sourceBufferPtr == IntPtr.Zero)
                        {
                            return;
                        }

                        //By default we'll only update the dirty rect, for those that run into a MILERR_WIN32ERROR Exception (#2035)
                        //it's desirably to either upgrade to a newer .Net version (only client runtime needs to be installed, not compiled
                        //against a newer version. Or invalidate the whole bitmap
                        if (invalidateDirtyRect)
                        {
                            // Update the dirty region
                            var sourceRect = new Int32Rect(dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);

                            bitmap.Lock();
                            bitmap.WritePixels(sourceRect, sourceBufferPtr, noOfBytes, stride, dirtyRect.X, dirtyRect.Y);
                            bitmap.Unlock();
                        }
                        else
                        {
                            // Update whole bitmap
                            var sourceRect = new Int32Rect(0, 0, width, height);

                            bitmap.Lock();
                            bitmap.WritePixels(sourceRect, sourceBufferPtr, noOfBytes, stride);
                            bitmap.Unlock();
                        }
                    }
                }), dispatcherPriority);
            }
        }
    }
}
