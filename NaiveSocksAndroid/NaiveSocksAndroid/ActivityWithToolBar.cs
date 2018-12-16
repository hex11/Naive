using Android.OS;
using Android.Support.Design.Widget;
using Android.Support.V7.App;
using Android.Views;
using Android.Widget;
using R = NaiveSocksAndroid.Resource;
using Toolbar = Android.Support.V7.Widget.Toolbar;

namespace NaiveSocksAndroid
{
    public class ActivityWithToolBar : AppCompatActivity
    {
        protected CoordinatorLayout topView { get; private set; }
        protected Toolbar toolbar { get; private set; }
        private FrameLayout contentFrame;

        public Handler Handler { get; private set; }

        protected View realContentView { get; private set; }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            App.CheckInit();
            Handler = new Handler(ApplicationContext.MainLooper);

            SetTheme(R.Style.MyTheme_Light);

            base.OnCreate(savedInstanceState);

            SetContentView(R.Layout.with_toolbar);

            topView = FindViewById<CoordinatorLayout>(R.Id.topview);
            contentFrame = FindViewById<FrameLayout>(R.Id.realContent);

            toolbar = FindViewById<Toolbar>(R.Id.toolbar);
            SetSupportActionBar(toolbar);
            SupportActionBar.SetHomeButtonEnabled(true);
        }

        protected void SetRealContentView(int layoutResId)
        {
            contentFrame.RemoveAllViews();
            realContentView = LayoutInflater.Inflate(layoutResId, contentFrame);
        }

        protected void SetRealContentView(View view)
        {
            contentFrame.RemoveAllViews();
            contentFrame.AddView(view);
        }

        public Snackbar MakeSnackbar(string text, int duration)
        {
            return Snackbar.Make(topView, text, duration);
        }

        public Snackbar MakeSnackbar(int textResId, int duration)
        {
            return Snackbar.Make(topView, textResId, duration);
        }
    }
}
