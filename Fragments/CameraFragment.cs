﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware.Display;
using Android.Media;
using Android.Net;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.ConstraintLayout.Widget;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using AndroidX.Lifecycle;
using AndroidX.LocalBroadcastManager.Content;
using AndroidX.Navigation;
using AndroidX.Window.Layout;
using Bumptech.Glide;
using Bumptech.Glide.Request;
using CameraXBasic.Utils;
using Java.Lang;

// Helper type alias used for analysis use case callbacks
delegate void LumaListener(double luma);

namespace CameraXBasic.Fragments
{
    // Main fragment for this app. Implements all camera operations including:
    // - Viewfinder
    // - Photo taking
    // - Image analysis
    [Android.App.Activity(Name = "com.android.example.cameraxbasic.fragments.CameraFragment")]
    public class CameraFragment : Fragment,
        IObserver,
        DisplayManager.IDisplayListener,
        View.IOnClickListener,
        MediaScannerConnection.IOnScanCompletedListener,
        ImageCapture.IOnImageSavedCallback,
        Java.IO.IFileFilter
    {
        private ConstraintLayout container;
        private PreviewView viewFinder;
        private Java.IO.File outputDirectory;
        private LocalBroadcastManager broadcastManager;

        private int displayId = -1;
        private int lensFacing = CameraSelector.LensFacingBack;
        private Preview preview;
        private ImageCapture imageCapture;
        private ImageAnalysis imageAnalyzer;
        private ICamera camera;
        private ProcessCameraProvider cameraProvider;
        private IWindowMetricsCalculator windowMetricsCalculator;

        private DisplayManager displayManager => RequireContext().GetSystemService(Context.DisplayService) as DisplayManager;

        // Blocking camera operations are performed using this executor
        private Java.Util.Concurrent.IExecutorService cameraExecutor;

        private VolumeDownReceiver volumeDownReceiver;

        // Volume down button receiver used to trigger shutter
        private class VolumeDownReceiver : BroadcastReceiver
        {
            private CameraFragment parent;

            public VolumeDownReceiver(CameraFragment fragment)
            {
                parent = fragment;
            }

            public override void OnReceive(Context context, Intent intent)
            {
                // When the volume down button is pressed, simulate a shutter button click
                if (intent.GetIntExtra(MainActivity.KeyEventExtra, (int) Keycode.Unknown) == (int) Keycode.VolumeDown)
                {
                    var shutter = parent.container.FindViewById<ImageButton>(Resource.Id.camera_capture_button);
                    shutter.SimulateClick();
                }
            }
        }

        // We need a display listener for orientation changes that do not trigger a configuration
        // change, for example if we choose to override config change in manifest or for 180-degree
        // orientation changes.
        public void OnDisplayAdded(int id)
        {
        }

        public void OnDisplayChanged(int id)
        {
            if (id == displayId)
            {
                Log.Debug(Tag, "Rotation changed: " + View.Display.Rotation);
                imageCapture.TargetRotation = (int) View.Display.Rotation;
                imageAnalyzer.TargetRotation = (int) View.Display.Rotation;
            }
        }

        public void OnDisplayRemoved(int id)
        {
        }

        public override void OnResume()
        {
            base.OnResume();

            // Make sure that all permissions are still present, since the
            // user could have removed them while the app was in paused state.
            if (!PermissionsFragment.HasPermissions(RequireContext()))
            {
                Navigation.FindNavController(RequireActivity(), Resource.Id.fragment_container).Navigate(Resource.Id.action_camera_to_permissions);
            }
        }

        public override void OnDestroyView()
        {
            base.OnDestroyView();

            // Shut down our background executor
            cameraExecutor.Shutdown();

            // Unregister the broadcast receivers and listeners
            broadcastManager.UnregisterReceiver(volumeDownReceiver);
            displayManager.UnregisterDisplayListener(this);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            return inflater.Inflate(Resource.Layout.fragment_camera, container, false);
        }

        private void SetGalleryThumbnail(Uri uri)
        {
            // Reference of the view that holds the gallery thumbnail
            ImageButton thumbnail = container.FindViewById<ImageButton>(Resource.Id.photo_view_button);

            // Run the operations in the view's thread
            thumbnail.Post(() =>
            {
                // Remove thumbnail padding
                int padding = (int) Resources.GetDimension(Resource.Dimension.stroke_small);
                thumbnail.SetPadding(padding, padding, padding, padding);

                // Load thumbnail into circular button using Glide
                Glide.With(thumbnail)
                    .Load(uri)
                    .Apply(RequestOptions.CircleCropTransform())
                    .Into(thumbnail);
            });
        }

        public override void OnViewCreated(View view, Bundle savedInstanceState)
        {
            base.OnViewCreated(view, savedInstanceState);

            container = view as ConstraintLayout;
            viewFinder = container.FindViewById<PreviewView>(Resource.Id.view_finder);

            // Initialize our background executor
            cameraExecutor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor();

            volumeDownReceiver = new VolumeDownReceiver(this);

            broadcastManager = LocalBroadcastManager.GetInstance(view.Context);

            // Set up the intent filter that will receive events from our main activity
            var filter = new IntentFilter();
            filter.AddAction(MainActivity.KeyEventAction);
            broadcastManager.RegisterReceiver(volumeDownReceiver, filter);

            // Every time the orientation of device changes, update rotation for use cases
            displayManager.RegisterDisplayListener(this, null);

            // Initialize WindowManager to retrieve display metrics
            windowMetricsCalculator = WindowMetricsCalculator.Companion.OrCreate;

            // Determine the output directory
            outputDirectory = MainActivity.GetOutputDirectory(RequireContext());

            // Wait for the views to be properly laid out
            viewFinder.Post(() =>
            {
                // Keep track of the display in which this view is attached
                displayId = viewFinder.Display.DisplayId;

                // Build UI controls
                UpdateCameraUi();

                // Set up the camera and its use cases
                SetUpCamera();
            });
        }

        // Inflate camera controls and update the UI manually upon config changes to avoid removing
        // and re-adding the view finder from the view hierarchy; this provides a seamless rotation
        // transition on devices that support it.
        //
        // NOTE: The flag is supported starting in Android 8 but there still is a small flash on the
        // screen for devices that run Android 9 or below.
        public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);

            // Rebind the camera with the updated display metrics
            BindCameraUseCases();

            // Enable or disable switching between cameras
            UpdateCameraSwitchButton();
        }

        // Initialize CameraX, and prepare to bind the camera use cases 
        private void SetUpCamera()
        {
            var cameraProviderFuture = ProcessCameraProvider.GetInstance(RequireContext());
            cameraProviderFuture.AddListener(new Java.Lang.Runnable(() =>
            {
                // CameraProvider
                cameraProvider = cameraProviderFuture.Get() as ProcessCameraProvider;

                // Select lensFacing depending on the available cameras
                if (HasBackCamera())
                    lensFacing = CameraSelector.LensFacingBack;
                else if (HasFrontCamera())
                    lensFacing = CameraSelector.LensFacingFront;
                else
                    throw new Java.Lang.IllegalStateException("Back and front camera are unavailable");

                // Enable or disable switching between cameras
                UpdateCameraSwitchButton();

                // Build and bind the camera use cases
                BindCameraUseCases();
            }), ContextCompat.GetMainExecutor(RequireContext()));
        }

        // Declare and bind preview, capture and analysis use cases
        private void BindCameraUseCases()
        {
            // Get screen metrics used to setup camera for full screen resolution
            var metrics = windowMetricsCalculator.ComputeCurrentWindowMetrics(RequireActivity()).Bounds;
            Log.Debug(Tag, "Screen metrics: " + metrics);

            var screenAspectRatio = AspectRatio(metrics.Width(), metrics.Height());
            Log.Debug(Tag, "Preview aspect ratio: " + screenAspectRatio);

            var rotation = viewFinder.Display.Rotation;

            // CameraProvider
            if (cameraProvider == null)
                throw new Java.Lang.IllegalStateException("Camera initialization failed.");

            // CameraSelector
            var cameraSelector = (new CameraSelector.Builder()).RequireLensFacing(lensFacing).Build();

            // Preview
            preview = new Preview.Builder()
                // We request aspect ratio but no resolution
                .SetTargetAspectRatio(screenAspectRatio)
                // Set initial target rotation
                .SetTargetRotation((int) rotation)
                .Build();

            // ImageCapture
            imageCapture = new ImageCapture.Builder()
                .SetCaptureMode(ImageCapture.CaptureModeMinimizeLatency)
                // We request aspect ratio but no resolution to match preview config, but letting
                // CameraX optimize for whatever specific resolution best fits our use cases
                .SetTargetAspectRatio(screenAspectRatio)
                // Set initial target rotation, we will have to call this again if rotation changes
                // during the lifecycle of this use case
                .SetTargetRotation((int) rotation)
                .Build();

            // ImageAnalysis
            imageAnalyzer = new ImageAnalysis.Builder()
                // We request aspect ratio but no resolution
                .SetTargetAspectRatio(screenAspectRatio)
                // Set initial target rotation, we will have to call this again if rotation changes
                // during the lifecycle of this use case
                .SetTargetRotation((int) rotation)
                .Build();

            // The analyzer can then be assigned to the instance
            imageAnalyzer.SetAnalyzer(cameraExecutor, new LuminosityAnalyzer(
                (double luma) => {
                    // Values returned from our analyzer are passed to the attached listener
                    // We log image analysis results here - you should do something useful
                    // instead!
                    Log.Debug(Tag, "Average luminosity: " + luma.ToString("0.00"));
                })
            );

            // Must unbind the use-cases before rebinding them
            cameraProvider.UnbindAll();
    
            try
            {
                // A variable number of use-cases can be passed here -
                // camera provides access to CameraControl & CameraInfo
                camera = cameraProvider.BindToLifecycle(
                    (ILifecycleOwner) this, cameraSelector, preview, imageCapture, imageAnalyzer);

                // Attach the viewfinder's surface provider to preview use case
                preview?.SetSurfaceProvider(viewFinder.SurfaceProvider);
                ObserveCameraState(camera?.CameraInfo);
            }
            catch (Java.Lang.Exception exc)
            {
                Log.Error(Tag, "Use case binding failed: " + exc);
            }
        }

        private void ObserveCameraState(ICameraInfo cameraInfo)
        {
            cameraInfo.CameraState.Observe(
                ViewLifecycleOwner, this);
        }

        public void OnChanged(Object p0)
        {
            var cameraState = p0 as CameraState;

            if (cameraState.GetType() == CameraState.Type.PendingOpen)
            {
                // Ask the user to close other camera apps
                Toast.MakeText(Context,
                    "CameraState: Pending Open",
                    ToastLength.Short).Show();
            }
            else if (cameraState.GetType() == CameraState.Type.Opening)
            {
                // Show the Camera UI
                Toast.MakeText(Context,
                    "CameraState: Opening",
                    ToastLength.Short).Show();
            }
            else if (cameraState.GetType() == CameraState.Type.Open)
            {
                // Setup Camera resources and begin processing
                Toast.MakeText(Context,
                    "CameraState: Open",
                    ToastLength.Short).Show();
            }
            else if (cameraState.GetType() == CameraState.Type.Closing)
            {
                // Close camera UI
                Toast.MakeText(Context,
                    "CameraState: Closing",
                    ToastLength.Short).Show();
            }
            else if (cameraState.GetType() == CameraState.Type.Closed)
            {
                // Free camera resources
                Toast.MakeText(Context,
                    "CameraState: Closed",
                    ToastLength.Short).Show();
            }

            if (cameraState.Error != null)
            {
                // Open errors
                if (cameraState.Error.Code == CameraState.ErrorStreamConfig)
                {
                    // Make sure to setup the use cases properly
                    Toast.MakeText(Context,
                        "Stream config error",
                        ToastLength.Short).Show();
                }
                // Opening errors
                else if (cameraState.Error.Code == CameraState.ErrorCameraInUse)
                {
                    // Close the camera or ask user to close another camera app that's using the
                    // camera
                    Toast.MakeText(Context,
                        "Camera in use",
                        ToastLength.Short).Show();
                }
                else if (cameraState.Error.Code == CameraState.ErrorMaxCamerasInUse)
                {
                    // Close another open camera in the app, or ask the user to close another
                    // camera app that's using the camera
                    Toast.MakeText(Context,
                        "Max cameras in use",
                        ToastLength.Short).Show();
                }
                else if (cameraState.Error.Code == CameraState.ErrorOtherRecoverableError)
                {
                    Toast.MakeText(Context,
                        "Other recoverable error",
                        ToastLength.Short).Show();
                }
                // Closing errors
                else if (cameraState.Error.Code == CameraState.ErrorCameraDisabled)
                {
                    // Ask the user to enable the device's cameras
                    Toast.MakeText(Context,
                        "Camera disabled",
                        ToastLength.Short).Show();
                }
                else if (cameraState.Error.Code == CameraState.ErrorCameraFatalError)
                {
                    // Ask the user to reboot the device to restore camera function
                    Toast.MakeText(Context,
                        "Fatal error",
                        ToastLength.Short).Show();
                }
                // Closed errors
                else if (cameraState.Error.Code == CameraState.ErrorDoNotDisturbModeEnabled)
                {
                    // Ask the user to disable the "Do Not Disturb" mode, then reopen the camera
                    Toast.MakeText(Context,
                        "Do not disturb mode enabled",
                        ToastLength.Short).Show();
                }
            }
        }

        // [androidx.camera.core.ImageAnalysis.Builder] requires enum value of
        // [androidx.camera.core.AspectRatio]. Currently it has values of 4:3 & 16:9.
        //
        //  Detecting the most suitable ratio for dimensions provided in @params by counting absolute
        //  of preview ratio to one of the provided values.
        //
        //  @param width - preview width
        //  @param height - preview height
        //  @return suitable aspect ratio
        private int AspectRatio(int width, int height)
        {
            var previewRatio = (double) System.Math.Max(width, height) / System.Math.Min(width, height);
            if (System.Math.Abs(previewRatio - Ratio4To3Value) <= System.Math.Abs(previewRatio - Ratio16To9Value))
            {
                return AndroidX.Camera.Core.AspectRatio.Ratio43;
            }
            return AndroidX.Camera.Core.AspectRatio.Ratio169;
        }

        public bool Accept(Java.IO.File file)
        {
            return GalleryFragment.ExtensionWhitelist.Contains(System.IO.Path.GetExtension(file.Path).ToLower());
        }

        // Method used to re-draw the camera UI controls, called every time configuration changes.
        private void UpdateCameraUi()
        {
            // Remove previous UI if any
            container.RemoveView(container.FindViewById<ConstraintLayout>(Resource.Id.camera_ui_container));

            // Inflate a new view containing all UI for controlling the camera
            var controls = View.Inflate(RequireContext(), Resource.Layout.camera_ui_container, container);

            // In the background, load latest photo taken (if any) for gallery thumbnail
            Task.Run(() =>
            {
                Java.IO.File[] files = outputDirectory.ListFiles(this);
                SetGalleryThumbnail(Uri.FromFile(files.Last()));
            });

            // Listener for button used to capture photo
            controls.FindViewById<ImageButton>(Resource.Id.camera_capture_button).SetOnClickListener(this);

            // Setup for button used to switch cameras
            var button = controls.FindViewById<ImageButton>(Resource.Id.camera_switch_button);

            // Disable the button until the camera is set up
            button.Enabled = false;

            // Listener for button used to switch cameras. Only called if the button is enabled
            button.SetOnClickListener(this);

            // Listener for button used to view the most recent photo
            controls.FindViewById<ImageButton>(Resource.Id.photo_view_button).SetOnClickListener(this);
        }

        private Java.IO.File photoFile;

        public void OnClick(View v)
        {
            if (v.Id == Resource.Id.camera_capture_button)
            {
                // Get a stable reference of the modifiable image capture use case
                if (imageCapture != null)
                {
                    // Create output file to hold the image
                    photoFile = CreateFile(outputDirectory, Filename, PhotoExtension);

                    // Setup image capture metadata
                    var metadata = new ImageCapture.Metadata();

                    // Mirror image when using the front camera
                    metadata.ReversedHorizontal = lensFacing == CameraSelector.LensFacingFront;

                    // Create output options object which contains file + metadata
                    var outputOptions = new ImageCapture.OutputFileOptions.Builder(photoFile)
                        .SetMetadata(metadata)
                        .Build();

                    // Setup image capture listener which is triggered after photo has been taken
                    imageCapture.TakePicture(outputOptions, cameraExecutor, this);
                }

                // We can only change the foreground Drawable using API level 23+ API
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    // Display flash animation to indicate that photo was captured
                    container.PostDelayed(() =>
                    {
                        container.Foreground = new ColorDrawable(Color.White);
                        container.PostDelayed(() =>
                        {
                            container.Foreground = null;
                        }, ViewExtensions.AnimationFastMillis);
                    }, ViewExtensions.AnimationSlowMillis);
                }
            }
            else if (v.Id == Resource.Id.camera_switch_button)
            {
                lensFacing = lensFacing == CameraSelector.LensFacingFront ?
                    CameraSelector.LensFacingBack :
                    CameraSelector.LensFacingFront;
                // Re-bind use cases to update selected camera
                BindCameraUseCases();
            }
            else if (v.Id == Resource.Id.photo_view_button)
            {
                // Only navigate when the gallery has photos
                if (true == outputDirectory.ListFiles().Length > 0)
                {
                    var args = new Bundle();
                    args.PutString("root_directory", outputDirectory.AbsolutePath);
                    Navigation.FindNavController(
                        RequireActivity(), Resource.Id.fragment_container
                    ).Navigate(Resource.Id.action_camera_to_gallery, args);
                }
            }
        }

        public void OnError(ImageCaptureException exc)
        {
            Log.Error(Tag, "Photo capture failed: " + exc);
        }

        public void OnImageSaved(ImageCapture.OutputFileResults output)
        {
            var savedUri = output.SavedUri != null ? output.SavedUri :
                Uri.FromFile(photoFile);
            Log.Debug(Tag, "Photo capture succeeded: " + savedUri);

            // We can only change the foreground Drawable using API level 23+ API
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                // Update the gallery thumbnail with latest picture taken
                SetGalleryThumbnail(savedUri);
            }

            // Implicit broadcasts will be ignored for devices running API level >= 24
            // so if you only target API level 24+ you can remove this statement
            if (Build.VERSION.SdkInt < BuildVersionCodes.N)
            {
                RequireActivity().SendBroadcast(new
                    Intent(Android.Hardware.Camera.ActionNewPicture, savedUri));
            }

            // If the folder selected is an external media directory, this is
            // unnecessary but otherwise other apps will not be able to access our
            // images unless we scan them using [MediaScannerConnection]
            var mimeType = MimeTypeMap.Singleton
                .GetMimeTypeFromExtension(System.IO.Path.GetExtension(savedUri.Path));

            MediaScannerConnection.ScanFile(
                Context,
                new string[] { savedUri.Path },
                new string[] { mimeType },
                this);
        }

        public void OnScanCompleted(string Path, Uri uri)
        {
            Log.Debug(Tag, "Image capture scanned into media store: " + uri);
        }

        // Enabled or disabled a button to switch cameras depending on the available cameras
        private void UpdateCameraSwitchButton()
        {
            var switchCamerasButton = container.FindViewById<ImageButton>(Resource.Id.camera_switch_button);
            try
            {
                switchCamerasButton.Enabled = HasBackCamera() && HasFrontCamera();
            }
            catch (CameraInfoUnavailableException)
            {
                switchCamerasButton.Enabled = false;
            }
        }

        // Returns true if the device has an available back camera. False otherwise
        private bool HasBackCamera()
        {
            return cameraProvider == null ? false :
                cameraProvider.HasCamera(CameraSelector.DefaultBackCamera);
        }

        // Returns true if the device has an available front camera. False otherwise
        private bool HasFrontCamera()
        {
            return cameraProvider == null ? false :
                cameraProvider.HasCamera(CameraSelector.DefaultFrontCamera);
        }

        // Our custom image analysis class.
        //
        // <p>All we need to do is override the function `analyze` with our desired operations. Here,
        // we compute the average luminosity of the image by looking at the Y plane of the YUV frame.
        //
        private class LuminosityAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
        {
            private int frameCounter = 0;
            private int frameRateWindow = 8;
            private Queue<long> frameTimestamps = new Queue<long>(5);
            private LumaListener[] listeners;
            private long lastAnalyzedTimestamp = 0L;
            private double framesPerSecond = -1.0;
            private byte[] data;

            public LuminosityAnalyzer(LumaListener listener)
                : base()
            {
                listeners = new LumaListener[] { listener };
            }

            // Used to add listeners that will be called with each luma computed
            void OnFrameAnalyzed(LumaListener listener)
            {
                listeners.Append(listener);
            }

            // Helper extension function used to extract a byte array from an image plane buffer
            void ToByteArray(Java.Nio.ByteBuffer byteBuffer)
            {
                byteBuffer.Rewind();    // Rewind the buffer to zero
                if (data == null || data.Length != byteBuffer.Remaining())
                {
                    // The int buffer is initialized only once the analyzer has started running
                    data = new byte[byteBuffer.Remaining()];
                }
                byteBuffer.Get(data);   // Copy the buffer into a byte array
            }

            // Analyzes an image to produce a result.
            //
            // <p>The caller is responsible for ensuring this analysis method can be executed quickly
            // enough to prevent stalls in the image acquisition pipeline. Otherwise, newly available
            // images will not be acquired and analyzed.
            //
            // <p>The image passed to this method becomes invalid after this method returns. The caller
            // should not store external references to this image, as these references will become
            // invalid.
            //
            // @param image image being analyzed VERY IMPORTANT: Analyzer method implementation must
            // call image.close() on received images when finished using them. Otherwise, new images
            // may not be received or the camera may stall, depending on back pressure setting.
            //
            public void Analyze(IImageProxy image)
            {
                // If there are no listeners attached, we don't need to perform analysis
                if (listeners.Length == 0)
                {
                    image.Close();
                    return;
                }

                // Keep track of frames analyzed
                var currentTime = Java.Lang.JavaSystem.CurrentTimeMillis();
                frameTimestamps.Enqueue(currentTime);

                // Compute the FPS using a moving average
                while (frameTimestamps.Count > frameRateWindow) frameTimestamps.Dequeue();
                var timestampFirst = frameTimestamps.First();
                var timestampLast = frameTimestamps.Last();
                framesPerSecond = 1.0 / ((timestampLast - timestampFirst) /
                    System.Math.Max(1, frameTimestamps.Count - 1)) * 1000.0;
                if (++frameCounter % frameRateWindow == 0)
                {
                    Log.Debug(Tag, "Frames per second: " + framesPerSecond.ToString("0.00"));
                }

                // Analysis could take an arbitrarily long amount of time
                // Since we are running in a different thread, it won't stall other use cases

                lastAnalyzedTimestamp = frameTimestamps.Last();

                // Since format in ImageAnalysis is YUV, image.planes[0] contains the luminance plane
                var buffer = image.GetPlanes()[0].Buffer;

                // Extract image data from callback object
                ToByteArray(buffer);

                // Compute average luminance for the image
                double luma = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    luma += data[i];
                }
                luma /= data.Length;

                // Call all listeners with new value
                foreach (LumaListener item in listeners)
                {
                    item(luma);
                }

                image.Close();
            }
        }

        private new const string Tag = "CameraXBasic";
        private const string Filename = "yyyy-MM-dd-HH-mm-ss-SSS";
        private const string PhotoExtension = ".jpg";
        private const double Ratio4To3Value = 4.0 / 3.0;
        private const double Ratio16To9Value = 16.0 / 9.0;

        // Helper function used to create a timestamped file
        private Java.IO.File CreateFile(Java.IO.File baseFolder, string format, string extension)
        {
            return new Java.IO.File(baseFolder, new Java.Text.SimpleDateFormat(format, Java.Util.Locale.Us)
                .Format(Java.Lang.JavaSystem.CurrentTimeMillis()) + extension);
        }
    }
}
