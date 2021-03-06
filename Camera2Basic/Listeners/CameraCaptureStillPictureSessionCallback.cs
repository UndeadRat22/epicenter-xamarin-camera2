using Android.Hardware.Camera2;
using Android.Util;

namespace Camera2Basic.Listeners
{
    public class CameraCaptureStillPictureSessionCallback : CameraCaptureSession.CaptureCallback
    {
        private static readonly string TAG = "CameraCaptureStillPictureSessionCallback";

        private readonly CameraFragment owner;

        public CameraCaptureStillPictureSessionCallback(CameraFragment owner)
        {
            this.owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            // If something goes wrong with the save (or the handler isn't even 
            // registered, this code will toast a success message regardless...)
            //owner.ShowToast("Saved: " + owner.ImageFile);
            Log.Debug(TAG, owner.ImageFile.ToString());
            owner.UnlockFocus();
        }
    }
}
