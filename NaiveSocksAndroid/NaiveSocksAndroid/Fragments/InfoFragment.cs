using Android.Views;
using Android.Support.V4.App;
using System.Threading;
using Android.OS;
using Java.Lang;

namespace NaiveSocksAndroid
{
    public class InfoFragment : Fragment
    {
        Handler handler;
        int timerInterval = -1;
        Runnable callbackRunnable;

        public InfoFragment()
        {
            handler = new Handler();
            callbackRunnable = new Runnable(Callback);
        }

        public int TimerInterval
        {
            get => timerInterval;
            set {
                if (timerInterval == value)
                    return;
                if (timerInterval <= 0 && value > 0) {
                    handler.PostDelayed(callbackRunnable, value);
                    postOnTheFly = true;
                }
                timerInterval = value;
            }
        }

        bool postOnTheFly = false;

        private void Callback()
        {
            postOnTheFly = false;
            if (!IsVisible) {
                return;
            }
            OnUpdate();
            PostDelayed();
        }

        private void PostDelayed()
        {
            if (postOnTheFly)
                throw new System.Exception("PostDelayed() should not be called when postOnTheFly == true");
            if (timerInterval > 0) {
                handler.PostDelayed(callbackRunnable, timerInterval);
                postOnTheFly = true;
            }
        }

        private void PostUpdate()
        {
            handler.Post(() => {
                if (!IsVisible)
                    return;
                OnUpdate();
            });
        }

        protected virtual void OnUpdate()
        {
        }

        public override void OnStart()
        {
            base.OnStart();
            if (!postOnTheFly)
                PostDelayed();
        }

        public override void OnResume()
        {
            base.OnResume();
            PostUpdate();
        }
    }
}
