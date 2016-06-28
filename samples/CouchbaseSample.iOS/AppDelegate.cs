using System;
using System.Collections.Generic;
using System.Linq;
using Foundation;
using UIKit;
using Couchbase;
using System.Threading.Tasks;
using System.IO;
using CoreFoundation;
using Couchbase.Lite;
using Couchbase.Lite.Auth;

namespace CouchbaseSample
{
    // The UIApplicationDelegate for the application. This class is responsible for launchinusing er Interface of the application, as well as listening (and optionally responding) to 
    // application events from iOS.
    [Register ("AppDelegate")]
    public partial class AppDelegate : UIApplicationDelegate, ILoginDelegate, IUIAlertViewDelegate
    {
        void HandleOIDCCallback (Uri loginUrl, Uri authBaseUrl, OIDCLoginContinuation continuation)
        {

        }

        private static readonly Uri ServerDbUrl = new Uri ("http://us-west.testfest.couchbasemobile.com:4984/grocery-sync/");
        private const string LocalDocId = "user";
        private const string DatabaseName = "grocery-sync";

        // class-level declarations
        UINavigationController navigationController;
        UIWindow window;
        private Replication _pull;
        private Replication _push;
        private Database _database;
        private Exception _syncError;

        public string Username { get; private set; }

        //
        // This method is invoked when the application has loaded and is ready to run. In this 
        // method you should instantiate the window, load the UI into it and then make the window
        // visible.
        //
        // You have 17 seconds to return from this method, or iOS will terminate your application.
        //
        public override bool FinishedLaunching (UIApplication app, NSDictionary options)
        {
            if (!InitializeDatabase ()) {
                return false;
            }

            window = new UIWindow (UIScreen.MainScreen.Bounds);

            Couchbase.Lite.Storage.SystemSQLite.Plugin.Register();

            OIDCViewController.FinishedLaunching (app, options);
            var controller = new OIDCViewController();
            window.TintColor = UIColor.FromRGB(0.564f, 0.0f, 0.015f);
            controller.EdgesForExtendedLayout = UIRectEdge.None;
            controller.Delegate = this;

            navigationController = new UINavigationController (controller);
            window.RootViewController = navigationController;
            window.MakeKeyAndVisible ();

            return true;
        }

        public override bool OpenUrl (UIApplication application, NSUrl url, string sourceApplication, NSObject annotation)
        {
            return OIDCViewController.OpenUrl (application, url, sourceApplication, annotation);
        }

        public void DidAuthCodeSignIn (OIDCViewController viewController)
        {
            StopReplication ();
            StartPull (repl => {
                repl.Authenticator = AuthenticatorFactory.CreateOpenIDAuthenticator (Manager.SharedInstance,
                                                                                    OIDCLoginController.LoginCallback);
                repl.Changed += MonitorOIDC;
            });
        }

        public async Task DidGoogleSignIn (OIDCViewController viewController, string idToken, NSError error)
        {
            if (error == null) {
                var result = await AuthenticateAsync (ServerDbUrl, idToken);
                if (result.Error == null && result.Username != null) {
                    DispatchQueue.MainQueue.DispatchAsync (() => {
                        if (Login (result.Username, result.SessionCookies)) {
                            CompleteLogin ();
                        }
                    });
                } else {
                    ShowAlert ("Authentication Failed", result.Error, false);
                }
            } else {
                ShowAlert ("Google SignIn Failed", error, false);
            }
        }

        public void DidLogout (OIDCViewController viewController)
        {
            
        }

        private void MonitorOIDC (object sender, ReplicationChangeEventArgs args)
        {
            var source = args.Source;
            var username = source.Authenticator?.Username;
            if (Username == null && username != null && IsReplicationStarted (source)) {
                source.Changed -= MonitorOIDC;
                if (Login (username)) {
                    if (_pull == source) {
                        DispatchQueue.MainQueue.DispatchAsync (() => {
                            StartPush (repl => {
                                repl.Authenticator = AuthenticatorFactory.CreateOpenIDAuthenticator (Manager.SharedInstance,
                                                                                                    OIDCLoginController.LoginCallback);
                                CompleteLogin ();
                            });
                        });
                    } else {
                        // When switching the user, the database and _pull replicator are
                        // reset so we need to restart the authenticating process again.
                        // The authenticating process from here should happen silently.
                        DidAuthCodeSignIn (null);
                    }
                }
            }
        }

        private static bool IsReplicationStarted (Replication repl)
        {
            return repl.Status == ReplicationStatus.Idle || repl.ChangesCount > 0;
        }

        public static void ShowAlert (string message, NSError error, bool fatal)
        {
            ShowAlert (message, error.LocalizedDescription, fatal);
        }

        public static void ShowAlert (string message, Exception e, bool fatal)
        {
            ShowAlert (message, e.ToString (), fatal);
        }

        public static void ShowAlert (string message, string error, bool fatal)
        {
            if (error != null) {
                message = $"{message}\n\n{error}";
            }

            var alert = new UIAlertView (fatal ? "Fatal Error" : "Error", message, null,
                                        fatal ? "Quit" : "Sorry");
            alert.Dismissed += (sender, e) => {
                if (fatal) {
                    Environment.Exit (0);
                }
            };
            alert.Show ();
        }

        private Task<AuthenticationResult> AuthenticateAsync (Uri remoteUrl, string token)
        {
            var sessionUrl = new Uri (remoteUrl, "_session");
            var request = new NSMutableUrlRequest (sessionUrl);
            request.HttpMethod = "POST";

            var authValue = $"Bearer {token}";
            var headers = (NSMutableDictionary)request.Headers.MutableCopy ();
            headers["Authorization"] = (NSString)authValue;
            request.Headers = headers;

            var tcs = new TaskCompletionSource<AuthenticationResult> ();
            var task = NSUrlSession.SharedSession.CreateDataTask (request, (data, response, error) => {
                var cookies = default (NSHttpCookie[]);
                var username = default (NSString);
                if (error == null) {
                    var httpRes = (NSHttpUrlResponse)response;
                    cookies = NSHttpCookie.CookiesWithResponseHeaderFields (httpRes.AllHeaderFields, remoteUrl);

                    var sessionData = (NSDictionary)NSJsonSerialization.Deserialize (data, NSJsonReadingOptions.MutableContainers, out error);
                    username = (NSString)((NSDictionary)sessionData ["userCtx"]) ["name"];
                }
                tcs.SetResult (new AuthenticationResult (cookies, username, error));
            });

            task.Resume ();
            return tcs.Task;
        }

        private bool InitializeDatabase ()
        {
            try {
                _database = Manager.SharedInstance.GetDatabase (DatabaseName);
                return true;
            } catch (Exception e) {
                ShowAlert ("Couldn't open database", e, true);
                return false;
            }
        }

        private void CompleteLogin ()
        {
            navigationController.PushViewController (new RootViewController () { Database = _database }, true);
        }

        private bool Login (string username, NSHttpCookie [] sessionCookies)
        {
            if (Login (username)) {
                StartPull (pull => {
                    
                    foreach (var cookie in sessionCookies) {
                        pull.SetCookie (cookie.Name, cookie.Value, cookie.Path, (DateTime)cookie.ExpiresDate, cookie.IsSecure, false);
                    }
                });
                StartPush (push => {
                    foreach (var cookie in sessionCookies) {
                        push.SetCookie (cookie.Name, cookie.Value, cookie.Path, (DateTime)cookie.ExpiresDate, cookie.IsSecure, false);
                    }
                });
            }

            return true;
        }

        private bool Login (string username)
        {
            var user = _database?.GetExistingLocalDocument (LocalDocId);
            if (_database != null && user != null && user ["username"] as string != username) {
                StopReplication ();
                _database.Delete ();
                _database = null;
            }

            if (_database == null) {
                if (!InitializeDatabase ()) {
                    return false;
                }
            }

            _database.PutLocalDocument (LocalDocId, new Dictionary<string, object> {
                ["username"] = username
            });
            Username = username;
            return true;
        }

        private void StartPull (Action<Replication> setup)
        {
            _pull = _database.CreatePullReplication (ServerDbUrl);
            _pull.Continuous = true;

            setup (_pull);

            _pull.Changed += ReplicationProgress;
            _pull.Start ();
        }

        private void StartPush (Action<Replication> setup)
        {
            _push = _database.CreatePushReplication (ServerDbUrl);
            _push.Continuous = true;

            setup (_push);

            _push.Changed += ReplicationProgress;
            _push.Start ();
        }

        private void StopReplication ()
        {
            if (_pull != null) {
                _pull.Stop ();
                _pull.Changed -= ReplicationProgress;
                _pull = null;
            }

            if (_push != null) {
                _push.Stop ();
                _push.Changed -= ReplicationProgress;
                _push = null;
            }

            UIApplication.SharedApplication.NetworkActivityIndicatorVisible = false;
        }

        private void ReplicationProgress(object sender, ReplicationChangeEventArgs args)
        {
            UIApplication.SharedApplication.NetworkActivityIndicatorVisible = 
                _pull.Status == ReplicationStatus.Active || _push.Status == ReplicationStatus.Active;

            var error = _pull.LastError ?? _push.LastError;
            if (error != _syncError) {
                _syncError = error;
                ShowAlert ("Sync Error", error, false);
            }
        }
    }

    public class AuthenticationResult
    {
        public NSHttpCookie[] SessionCookies { get; }

        public NSString Username { get; }

        public NSError Error { get; }

        public AuthenticationResult (NSHttpCookie[] sessionCookies, NSString username, NSError error)
        {
            SessionCookies = sessionCookies;
            Username = username;
            Error = error;
        }
    }
}

