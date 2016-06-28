//
// OIDCUIViewController.cs
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
using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;

namespace CouchbaseSample
{
    public class OIDCUIViewController : UIViewController, IWKNavigationDelegate
    {
        private readonly OIDCLoginController _controller;
        private WKWebView _webView;

        public OIDCUIViewController (OIDCLoginController controller)
        {
            _controller = controller;
        }

        private void Cancel (object sender, EventArgs args)
        {
            _webView.StopLoading ();
            _controller.Delegate?.OpenIDControllerDidCancel (_controller);
        }

        public override void LoadView ()
        {
            var rootView = new UIView (new CGRect (0, 0, 200, 200));
            _webView = new WKWebView (rootView.Bounds, new WKWebViewConfiguration ());
            _webView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
            _webView.NavigationDelegate = new OIDCNavigationDelegate (_controller);
            rootView.AddSubview (_webView);

            View = rootView;
        }

        public override void ViewDidLoad ()
        {
            base.ViewDidLoad ();

            Title = "Log In With OpenID";
            var cancelButton = new UIBarButtonItem ("Cancel", UIBarButtonItemStyle.Plain, Cancel);
            NavigationItem.RightBarButtonItem = cancelButton;
        }

        public override void ViewWillAppear (bool animated)
        {
            base.ViewWillAppear (animated);
            _webView.LoadRequest (new NSUrlRequest (_controller.LoginUrl));
        }
    }

    internal class OIDCNavigationDelegate : WKNavigationDelegate
    {
        private readonly OIDCLoginController _controller;

        public OIDCNavigationDelegate (OIDCLoginController controller)
        {
            _controller = controller;
        }

        public override void DecidePolicy (WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var navigate = _controller.NavigateToUrl (navigationAction.Request.Url);
            decisionHandler (navigate ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }
    }
}

