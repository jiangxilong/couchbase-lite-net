//
// OIDCLoginController.cs
//
// Author:
// 	Jim Borden  <jim.borden@couchbase.com>
//
// Copyright (c) 2016 Couchbase, Inc All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using System;
using Couchbase.Lite.Auth;
using Foundation;
using UIKit;

namespace CouchbaseSample
{
    public interface IOIDCLoginControllerDelegate
    {
        void OpenIDControllerDidCancel (OIDCLoginController controller);

        void OpenIDController (OIDCLoginController controller, string error, string description);

        void OpenIDController (OIDCLoginController controller, Uri url);
    }

    public sealed class OIDCLoginController : IOIDCLoginControllerDelegate
    {
        private readonly string _redirectURL;
        private readonly OIDCLoginContinuation _callback;
        private UIViewController _presentedUI;
        private UIViewController _UIController;

        public static OIDCCallback LoginCallback {
            get {
                return (loginURL, redirectURL, callback) => new OIDCLoginController (loginURL, redirectURL, callback);
            }
        }

        public Uri LoginUrl { get; }

        public IOIDCLoginControllerDelegate Delegate { get; }

        private UIViewController ViewController {
            get {
                if (_UIController == null) {
                    _UIController = new OIDCUIViewController (this);
                }

                return _UIController;
            }
        }

        private OIDCLoginController (Uri loginURL, Uri redirectURL, IOIDCLoginControllerDelegate delegateObj)
        {
            LoginUrl = loginURL;
            _redirectURL = redirectURL.AbsoluteUri;
            Delegate = delegateObj;
        }

        private OIDCLoginController (Uri loginURL, Uri redirectURL, OIDCLoginContinuation callback) :
            this(loginURL, redirectURL, (IOIDCLoginControllerDelegate)null)
        {
            Delegate = this;
            _callback = callback;
            PresentUI ();
        }

        internal bool NavigateToUrl (Uri url)
        {
            if (!url.AbsoluteUri.StartsWith (_redirectURL, StringComparison.InvariantCulture)) {
                return true; // Ordinary URL, let the WebView handle it
            }

            // Look at the URL query to see if it's an error or not:
            var error = default (string);
            var description = default (string);
            var comp = new NSUrlComponents (url, true);
            foreach (var item in comp.QueryItems) {
                if (item.Name == "error") {
                    error = item.Value;
                } else if (item.Name == "error_description") {
                    description = item.Value;
                }
            }

            if (error != null) {
                Delegate?.OpenIDController (this, error, description);
            } else {
                Delegate?.OpenIDController (this, url);
            }

            return false;
        }

        private void PresentUI ()
        {
            var parent = UIApplication.SharedApplication.KeyWindow.RootViewController;
            _presentedUI = PresentViewControllerIn (parent as UINavigationController);
        }

        private void CloseUI ()
        {
            _presentedUI.DismissViewController (true, () => {
                _presentedUI = null;
            });
        }

        private UINavigationController PresentViewControllerIn (UIViewController parent)
        {
            var viewController = ViewController;
            var navController = new UINavigationController (viewController);
            if (UIDevice.CurrentDevice.UserInterfaceIdiom != UIUserInterfaceIdiom.Phone) {
                navController.ModalPresentationStyle = UIModalPresentationStyle.FormSheet;
            }

            parent.PresentViewController (navController, true, null);
            return navController;
        }

        public void OpenIDControllerDidCancel (OIDCLoginController controller)
        {
            _callback (null, null);
            CloseUI ();
        }

        public void OpenIDController (OIDCLoginController controller, string error, string description)
        {
            var info = new NSDictionary (NSError.LocalizedDescriptionKey, error, 
                                         NSError.LocalizedFailureReasonErrorKey, description ?? "Login Failed");
            var errorObject = new NSError (NSError.NSUrlErrorDomain, (int)NSUrlError.Unknown, info);
            _callback (null, new NSErrorException(errorObject));
            CloseUI ();
        }

        public void OpenIDController (OIDCLoginController controller, Uri url)
        {
            _callback (url, null);
            CloseUI ();
        }
    }
}

