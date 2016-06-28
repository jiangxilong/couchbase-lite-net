//
// OIDCViewController.cs
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

using UIKit;
using Google.SignIn;
using Foundation;
using Google.Core;

namespace CouchbaseSample
{
    public partial class OIDCViewController : UIViewController, ISignInUIDelegate
    {

        private const string GoogleClientId = "31919031332-sjiopc9dnh217somhc94b3s1kt7oe2mu.apps.googleusercontent.com";


        public ILoginDelegate Delegate { get; set; }

        public OIDCViewController () : base ("OIDCViewController", null)
        {
            
        }

        public static void FinishedLaunching (UIApplication app, NSDictionary options)
        {
            NSError configureError;
            Context.SharedInstance.Configure (out configureError);
            if (configureError != null) {
                Console.WriteLine("Error configuring the Google context: {0}", configureError);
            }

            SignIn.SharedInstance.ClientID = GoogleClientId;
            SignIn.SharedInstance.ServerClientID = GoogleClientId;
        }

        public static bool OpenUrl (UIApplication application, NSUrl url, string sourceApplication, NSObject annotation)
        {
            return SignIn.SharedInstance.HandleUrl (url, sourceApplication, annotation);
        }

        private void AuthButtonTouched (object sender, EventArgs args)
        {
            Delegate?.DidAuthCodeSignIn (this);
        }

        public override void ViewDidLoad ()
        {
            base.ViewDidLoad ();

            var signIn = SignIn.SharedInstance;
            signIn.ShouldFetchBasicProfile = true;
            signIn.UIDelegate = this;

            signIn.SignedIn += (sender, e) => {
                Delegate?.DidGoogleSignIn (this, e.User?.Authentication?.IdToken, e.Error);
            };

            signIn.Disconnected += (sender, e) => {
                Console.WriteLine ("Google SignIn : user diconnected.");
            };

            if (signIn.HasAuthInKeychain) {
                signIn.SignInUserSilently ();
            }

            OIDCAuthButton.TouchUpInside += AuthButtonTouched;
        }

        public override void DidReceiveMemoryWarning ()
        {
            base.DidReceiveMemoryWarning ();
            // Release any cached data, images, etc that aren't in use.
        }
    }
}


