﻿using System;
using System.Collections.Generic;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Android.Hardware.Camera2;
using Android.Graphics;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Support.V13.App;
using Android.Support.V4.Content;
using Camera2Basic.Listeners;
using Java.IO;
using Java.Lang;
using Java.Util;
using Java.Util.Concurrent;
using Boolean = Java.Lang.Boolean;
using Math = Java.Lang.Math;
using Observable = System.Reactive.Linq.Observable;
using Orientation = Android.Content.Res.Orientation;

namespace Camera2Basic
{
    public class CameraFragment : Fragment, View.IOnClickListener, FragmentCompat.IOnRequestPermissionsResultCallback
    {
        public static CameraFragment Instance { get; private set; }

        private static readonly SparseIntArray ORIENTATIONS = new SparseIntArray();
        public static readonly int REQUEST_CAMERA_PERMISSION = 1;
        private static readonly string FRAGMENT_DIALOG = "dialog";

        private static readonly string TAG = "CameraFragment";

        public const int STATE_PREVIEW = 0;
        public const int STATE_WAITING_LOCK = 1;
        public const int STATE_WAITING_PRECAPTURE = 2;
        public const int STATE_WAITING_NON_PRECAPTURE = 3;
        public const int STATE_PICTURE_TAKEN = 4;

        private static readonly int MAX_PREVIEW_WIDTH = 1920;
        private static readonly int MAX_PREVIEW_HEIGHT = 1080;

        private string _cameraId;

        private Lazy<CameraStateListener> _stateCallback = new Lazy<CameraStateListener>(() => new CameraStateListener(Instance));
        private Lazy<SurfaceTextureListener> _surfaceTextureListener = new Lazy<SurfaceTextureListener>(() => new SurfaceTextureListener(Instance));
        private Lazy<ImageAvailableListener> _onImageAvailableListener = new Lazy<ImageAvailableListener>(() => new ImageAvailableListener(Instance));

        public CameraStateListener StateCallback { get { return _stateCallback.Value; } }
        public SurfaceTextureListener SurfaceTextureListener { get { return _surfaceTextureListener.Value; } }
        public ImageAvailableListener OnImageAvailableListener { get { return _onImageAvailableListener.Value; } }

        private AutoFitTextureView _textureView;

        public CameraCaptureSession CaptureSession { get; set; }
        public CameraDevice CameraDevice { get; set; }
        public Handler BackgroundHandler { get; set; }
        public CaptureRequest.Builder PreviewRequestBuilder { get; set; }
        public CaptureRequest PreviewRequest { get; set; }
        public CameraCaptureListener CaptureCallback { get; set; }

        public File ImageFile { get; private set; }

        public Point DisplaySize {
            get
            {
                Point displaySize = new Point();
                Activity.WindowManager.DefaultDisplay.GetSize(displaySize);
                return displaySize;
            }
        }

        private Size _previewSize;
        private HandlerThread _backgroundThread;
        private ImageReader _imageReader;

        public int CurrentCameraState { get; set; } = STATE_PREVIEW;

        public Semaphore CameraOpenCloseLock = new Semaphore(1);

        private bool _flashSupported;
        private int _sensorOrientation;

        private CaptureRequest.Builder stillCaptureBuilder;
        private Button _btnStartStop;



        private IDisposable _timelapseTimer;
        private string _imagesDirectory;


        public void ShowToast(string text)
        {
            if (Activity != null)
                Activity.RunOnUiThread(new ToastNotification(Activity.ApplicationContext, text));
        }

        private static Size ChooseOptimalSize(Size[] choices, int textureViewWidth,
            int textureViewHeight, int maxWidth, int maxHeight, Size aspectRatio)
        {
            // Collect the supported resolutions that are at least as big as the preview Surface
            List<Size> bigEnough = new List<Size>();
            // Collect the supported resolutions that are smaller than the preview Surface
            List<Size> notBigEnough = new List<Size>();
            int w = aspectRatio.Width;
            int h = aspectRatio.Height;

            foreach (Size option in choices)
            {
                if ((option.Width <= maxWidth) && (option.Height <= maxHeight) &&
                option.Height == option.Width * h / w)
                {
                    if (option.Width >= textureViewWidth &&
                        option.Height >= textureViewHeight)
                        bigEnough.Add(option);
                    else
                        notBigEnough.Add(option);
                }
            }

            // Pick the smallest of those big enough. If there is no one big enough, pick the
            // largest of those not big enough.
            if (bigEnough.Count > 0)
                return (Size)Collections.Min(bigEnough, new CompareSizesByArea());

            if (notBigEnough.Count > 0)
                return (Size)Collections.Max(notBigEnough, new CompareSizesByArea());

            Log.Error(TAG, "Couldn't find any suitable preview size");
            return choices[0];
        }
        public override void OnCreate(Bundle savedInstanceState)
        {
            Instance = this;
            base.OnCreate(savedInstanceState);

            // fill ORIENTATIONS list
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            return inflater.Inflate(Resource.Layout.fragment_camera2_basic, container, false);
        }

        public override void OnViewCreated(View view, Bundle savedInstanceState)
        {
            _textureView = (AutoFitTextureView)view.FindViewById(Resource.Id.texture);

            _btnStartStop = view.FindViewById<Button>(Resource.Id.picture); 
            _btnStartStop.SetOnClickListener(this);

            view.FindViewById(Resource.Id.info).SetOnClickListener(this);
        }

        public override void OnActivityCreated(Bundle savedInstanceState)
        {
            base.OnActivityCreated(savedInstanceState);
            _imagesDirectory = Activity.GetExternalFilesDir(null).AbsolutePath;

            CaptureCallback = new CameraCaptureListener(this);
        }

        public override void OnResume()
        {
            base.OnResume();
            StartBackgroundThread();

            // When the screen is turned off and turned back on, the SurfaceTexture is already
            // available, and "onSurfaceTextureAvailable" will not be called. In that case, we can open
            // a camera and start preview from here (otherwise, we wait until the surface is ready in
            // the SurfaceTextureListener).
            if (_textureView.IsAvailable)
                OpenCamera(_textureView.Width, _textureView.Height);
            else
                _textureView.SurfaceTextureListener = SurfaceTextureListener;
        }

        public override void OnPause()
        {
            CloseCamera();
            StopBackgroundThread();
            base.OnPause();
        }

        private void RequestCameraPermission()
        {
            if (FragmentCompat.ShouldShowRequestPermissionRationale(this, Manifest.Permission.Camera))
            {
                new ConfirmationDialog().Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
            else
            {
                FragmentCompat.RequestPermissions(this, new string[] { Manifest.Permission.Camera },
                        REQUEST_CAMERA_PERMISSION);
            }
        }

        public void OnRequestPermissionsResult(int requestCode, string[] permissions, int[] grantResults)
        {
            if (requestCode != REQUEST_CAMERA_PERMISSION)
                return;

            if (grantResults.Length != 1 || grantResults[0] != (int)Permission.Granted)
            {
                ErrorDialog.NewInstance(GetString(Resource.String.request_permission))
                        .Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
        }


        // Sets up member variables related to camera.
        private void SetUpCameraOutputs(int width, int height)
        {
            CameraManager manager = (CameraManager)Activity.GetSystemService(Context.CameraService);
            try
            {
                foreach (string cameraId in manager.GetCameraIdList())
                {
                    CameraCharacteristics characteristics = manager.GetCameraCharacteristics(cameraId);

                    Integer facing = (Integer)characteristics.Get(CameraCharacteristics.LensFacing);
                    if (facing != null && facing == (Integer.ValueOf((int)LensFacing.Front)))
                        continue;

                    StreamConfigurationMap map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    if (map == null)
                        continue;

                    // For still image captures, we use the largest available size.
                    Size largest = (Size)Collections.Max(Arrays.AsList(map.GetOutputSizes((int)ImageFormatType.Jpeg)),
                        new CompareSizesByArea());
                    _imageReader = ImageReader.NewInstance(largest.Width, largest.Height, ImageFormatType.Jpeg, 2);
                    _imageReader.SetOnImageAvailableListener(OnImageAvailableListener, BackgroundHandler);

                    _sensorOrientation = (int)characteristics.Get(CameraCharacteristics.SensorOrientation);
                    bool swapped = IsCameraRoationSameAsDevice(Activity.WindowManager.DefaultDisplay.Rotation, _sensorOrientation);

                    Size rotatedSize = GetRotatedPreviewSize(width, height, swapped);
                    Size maxSize = GetMaxPrieviewSize(width, height, swapped);

                    // Danger, W.R.! Attempting to use too large a preview size could  exceed the camera
                    // bus' bandwidth limitation, resulting in gorgeous previews but the storage of
                    // garbage capture data.
                    _previewSize = ChooseOptimalSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))),
                        rotatedSize.Width, rotatedSize.Height, maxSize.Width,
                        maxSize.Height, largest);

                    SetTextureViewAspectRatio();

                    // Check if the flash is supported.
                    Boolean available = (Boolean)characteristics.Get(CameraCharacteristics.FlashInfoAvailable);
                    _flashSupported = (available == null ? false : (bool)available);
 
                    _cameraId = cameraId;
                    return;
                }
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
            catch//camera_error
            {
                ErrorDialog.NewInstance(GetString(Resource.String.camera_error)).Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
        }

        // We fit the aspect ratio of TextureView to the size of preview we picked.
        private void SetTextureViewAspectRatio()
        {                    
            var orientation = Resources.Configuration.Orientation;
            if (orientation == Orientation.Landscape)
                _textureView.SetAspectRatio(_previewSize.Width, _previewSize.Height);
            else
                _textureView.SetAspectRatio(_previewSize.Height, _previewSize.Width);
        }

        private Size GetMaxPrieviewSize(int width, int height, bool dimentionsSwapped)
        {
            int maxPreviewWidth = DisplaySize.X;
            int maxPreviewHeight = DisplaySize.Y;

            if (dimentionsSwapped)
            {
                maxPreviewWidth = DisplaySize.Y;
                maxPreviewHeight = DisplaySize.X;
            }

            if (maxPreviewWidth > MAX_PREVIEW_WIDTH)
                maxPreviewWidth = MAX_PREVIEW_WIDTH;
            if (maxPreviewHeight > MAX_PREVIEW_HEIGHT)
                maxPreviewHeight = MAX_PREVIEW_HEIGHT;

            return new Size(maxPreviewWidth, maxPreviewHeight);
        }
        private Size GetRotatedPreviewSize(int width, int height, bool dimentionsSwapped)
        {
            int rotatedPreviewWidth = width;
            int rotatedPreviewHeight = height;

            if (dimentionsSwapped)
            {
                rotatedPreviewWidth = height;
                rotatedPreviewHeight = width;
            }

            return new Size(rotatedPreviewWidth, rotatedPreviewHeight);
        }

        private bool IsCameraRoationSameAsDevice(SurfaceOrientation displayRotation, int sensorOrientation)
        {
            switch (displayRotation)
            {
                case SurfaceOrientation.Rotation0:
                case SurfaceOrientation.Rotation180:
                    if (sensorOrientation == 90 || sensorOrientation == 270)
                        return true;
                    break;
                case SurfaceOrientation.Rotation90:
                case SurfaceOrientation.Rotation270:
                    if (sensorOrientation == 0 || sensorOrientation == 180)
                        return true;
                    break;
                default:
                    Log.Error(TAG, "Display rotation is invalid: " + displayRotation);
                    return false;
            }
            return false;
        }

        // Opens the camera specified by {@link CameraFragment#_cameraId}.
        public void OpenCamera(int width, int height)
        {
            if (ContextCompat.CheckSelfPermission(Activity, Manifest.Permission.Camera) != Permission.Granted)
            {
                RequestCameraPermission();
                return;
            }
            SetUpCameraOutputs(width, height);
            ConfigureTransform(width, height);
            var activity = Activity;
            var manager = (CameraManager)activity.GetSystemService(Context.CameraService);
            try
            {
                if (!CameraOpenCloseLock.TryAcquire(2500, TimeUnit.Milliseconds))
                {
                    throw new RuntimeException("Time out waiting to lock camera opening.");
                }
                manager.OpenCamera(_cameraId, StateCallback, BackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
            catch (InterruptedException e)
            {
                throw new RuntimeException("Interrupted while trying to lock camera opening.", e);
            }
        }

        // Closes the current {@link CameraDevice}.
        private void CloseCamera()
        {
            try
            {
                CameraOpenCloseLock.Acquire();
                if (null != CaptureSession)
                {
                    CaptureSession.Close();
                    CaptureSession = null;
                }
                if (null != CameraDevice)
                {
                    CameraDevice.Close();
                    CameraDevice = null;
                }
                if (null != _imageReader)
                {
                    _imageReader.Close();
                    _imageReader = null;
                }
            }
            catch (InterruptedException e)
            {
                throw new RuntimeException("Interrupted while trying to lock camera closing.", e);
            }
            finally
            {
                CameraOpenCloseLock.Release();
            }
        }

        // Starts a background thread and its {@link Handler}.
        private void StartBackgroundThread()
        {
            _backgroundThread = new HandlerThread("CameraBackground");
            _backgroundThread.Start();
            BackgroundHandler = new Handler(_backgroundThread.Looper);
        }

        // Stops the background thread and its {@link Handler}.
        private void StopBackgroundThread()
        {
            _backgroundThread.QuitSafely();
            try
            {
                _backgroundThread.Join();
                _backgroundThread = null;
                BackgroundHandler = null;
            }
            catch (InterruptedException e)
            {
                e.PrintStackTrace();
            }
        }

        // Creates a new {@link CameraCaptureSession} for camera preview.
        public void CreateCameraPreviewSession()
        {
            try
            {
                SurfaceTexture texture = _textureView.SurfaceTexture;
                if (texture == null)
                {
                    throw new IllegalStateException("texture is null");
                }

                // We configure the size of default buffer to be the size of camera preview we want.
                texture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);

                // This is the output Surface we need to start preview.
                Surface surface = new Surface(texture);

                // We set up a CaptureRequest.Builder with the output Surface.
                PreviewRequestBuilder = CameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                PreviewRequestBuilder.AddTarget(surface);

                // Here, we create a CameraCaptureSession for camera preview.
                List<Surface> surfaces = new List<Surface>();
                surfaces.Add(surface);
                surfaces.Add(_imageReader.Surface);
                CameraDevice.CreateCaptureSession(surfaces, new CameraCaptureSessionCallback(this), null);

            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        public static T Cast<T>(Java.Lang.Object obj) where T : class
        {
            var propertyInfo = obj.GetType().GetProperty("Instance");
            return propertyInfo == null ? null : propertyInfo.GetValue(obj, null) as T;
        }

        // Configures the necessary {@link android.graphics.Matrix}
        // transformation to `_textureView`.
        // This method should be called after the camera preview size is determined in
        // setUpCameraOutputs and also the size of `_textureView` is fixed.

        public void ConfigureTransform(int viewWidth, int viewHeight)
        {
            Activity activity = Activity;
            if (null == _textureView || null == _previewSize || null == activity)
            {
                return;
            }
            var rotation = (int)activity.WindowManager.DefaultDisplay.Rotation;
            Matrix matrix = new Matrix();
            RectF viewRect = new RectF(0, 0, viewWidth, viewHeight);
            RectF bufferRect = new RectF(0, 0, _previewSize.Height, _previewSize.Width);
            float centerX = viewRect.CenterX();
            float centerY = viewRect.CenterY();
            if ((int)SurfaceOrientation.Rotation90 == rotation || (int)SurfaceOrientation.Rotation270 == rotation)
            {
                bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
                matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
                float scale = Math.Max((float)viewHeight / _previewSize.Height, (float)viewWidth / _previewSize.Width);
                matrix.PostScale(scale, scale, centerX, centerY);
                matrix.PostRotate(90 * (rotation - 2), centerX, centerY);
            }
            else if ((int)SurfaceOrientation.Rotation180 == rotation)
            {
                matrix.PostRotate(180, centerX, centerY);
            }
            _textureView.SetTransform(matrix);
        }

        // Initiate a still image capture.
        private void TakePicture()
        {
            if (_btnStartStop.Text == Resources.GetString(Resource.String.start_picture))
            {
                _timelapseTimer =
                    Observable
                        .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(5))
                        .Subscribe(l =>
                        {
                            Activity.RunOnUiThread(() =>
                            {
                                if (l == 0)
                                {
                                    _btnStartStop.Text = Resources.GetString(Resource.String.stop_picture);
                                    _btnStartStop.SetBackgroundColor(Color.Red);
                                }
                                Log.Debug(TAG, $"Taking Picture at {DateTime.Now:T}");
                                string fileName = $"{DateTime.Now:s}".Replace('T', '-').Replace(':', '-');
                                ImageFile = new File(_imagesDirectory, $"{fileName}.jpg");
                                LockFocus();
                            });
                        });
            }
            else
            {
                if (_timelapseTimer == null) return;
                Log.Debug(TAG, $"Stopped Taking Pictures at {DateTime.Now:T}");

                _timelapseTimer.Dispose();
                _timelapseTimer = null;
                _btnStartStop.Text = Resources.GetString(Resource.String.start_picture);
                _btnStartStop.SetBackgroundColor(Color.Green);
            }
        }

        /// <summary>
        /// Lock the focus as the first step for a still image capture.
        /// </summary>
        private void LockFocus()
        {
            try
            {
                // This is how to tell the camera to lock focus.

                PreviewRequestBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Start);
                // Tell #CaptureCallback to wait for the lock.
                CurrentCameraState = STATE_WAITING_LOCK;
                // this will kick off the image-capture pipeline
                CaptureSession.Capture(PreviewRequestBuilder.Build(), CaptureCallback, BackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        // Run the precapture sequence for capturing a still image. This method should be called when
        // we get a response in {@link #CaptureCallback} from {@link #lockFocus()}.
        public void RunPrecaptureSequence()
        {
            try
            {
                // This is how to tell the camera to trigger.
                PreviewRequestBuilder.Set(CaptureRequest.ControlAePrecaptureTrigger, (int)ControlAEPrecaptureTrigger.Start);
                // Tell #CaptureCallback to wait for the precapture sequence to be set.
                CurrentCameraState = STATE_WAITING_PRECAPTURE;
                CaptureSession.Capture(PreviewRequestBuilder.Build(), CaptureCallback, BackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }


        // Capture a still picture. This method should be called when we get a response in
        // {@link #CaptureCallback} from both {@link #lockFocus()}.
        public void CaptureStillPicture()
        {
            try
            {
                var activity = Activity;
                if (null == activity || null == CameraDevice)
                {
                    return;
                }
                // This is the CaptureRequest.Builder that we use to take a picture.
                if(stillCaptureBuilder == null)
                    stillCaptureBuilder = CameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);

                stillCaptureBuilder.AddTarget(_imageReader.Surface);

                // Use the same AE and AF modes as the preview.
                stillCaptureBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                SetAutoFlash(stillCaptureBuilder);

                // Orientation
                int rotation = (int)activity.WindowManager.DefaultDisplay.Rotation;
                stillCaptureBuilder.Set(CaptureRequest.JpegOrientation, GetOrientation(rotation));

                CaptureSession.StopRepeating();
                CaptureSession.Capture(stillCaptureBuilder.Build(), new CameraCaptureStillPictureSessionCallback(this), null);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        // Retrieves the JPEG orientation from the specified screen rotation.
        private int GetOrientation(int rotation)
        {
            // Sensor orientation is 90 for most devices, or 270 for some devices (eg. Nexus 5X)
            // We have to take that into account and rotate JPEG properly.
            // For devices with orientation of 90, we simply return our mapping from ORIENTATIONS.
            // For devices with orientation of 270, we need to rotate the JPEG 180 degrees.
            return (ORIENTATIONS.Get(rotation) + _sensorOrientation + 270) % 360;
        }

        // Unlock the focus. This method should be called when still image capture sequence is
        // finished.
        public void UnlockFocus()
        {
            try
            {
                // Reset the auto-focus trigger
                PreviewRequestBuilder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Cancel);
                SetAutoFlash(PreviewRequestBuilder);
                CaptureSession.Capture(PreviewRequestBuilder.Build(), CaptureCallback,
                        BackgroundHandler);
                // After this, the camera will go back to the normal state of preview.
                CurrentCameraState = STATE_PREVIEW;
                CaptureSession.SetRepeatingRequest(PreviewRequest, CaptureCallback,
                        BackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        public void OnClick(View v)
        {
            switch (v.Id)
            {
                case Resource.Id.picture:
                    TakePicture();
                    break;
                case Resource.Id.info:
                    EventHandler<DialogClickEventArgs> nullHandler = null;
                    Activity activity = Activity;
                    if (activity != null)
                    {
                        new AlertDialog.Builder(activity)
                            .SetMessage("This sample demonstrates the basic use of the Camera2 API. ...")
                            .SetPositiveButton(Android.Resource.String.Ok, nullHandler)
                            .Show();
                    }
                    break;
            }
        }

        public void SetAutoFlash(CaptureRequest.Builder requestBuilder)
        {
            if (_flashSupported)
            {
                requestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
            }
        }
    }
}

