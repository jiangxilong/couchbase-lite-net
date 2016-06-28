//
// RootViewController2.cs
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoreGraphics;
using Couchbase.Lite;
using Couchbase.Lite.iOS;
using Foundation;
using Newtonsoft.Json.Linq;
using UIKit;

namespace CouchbaseSample
{
    public partial class RootViewController : UIViewController
    {
        private const string ReplicationChangeNotification = "CBLReplicationChange";
        private const string DefaultViewName = "byDate";
        private const string DocumentDisplayPropertyName = "text";
        internal const string CheckboxPropertyName = "check";
        internal const String CreationDatePropertyName = "created_at";
        internal const String DeletedKey = "_deleted";
        internal const string OwnerKey = "owner";
        CouchbaseTableSource Datasource = new CouchbaseTableSource ();

        UIProgressView Progress { get; set; }

        public Database Database { get; set; }

        LiveQuery DoneQuery { get; set; }

        #region Initialization/Configuration

        public RootViewController () : base ("RootViewController", null)
        {
            Title = NSBundle.MainBundle.LocalizedString ("Grocery Sync", "Grocery Sync");
        }

        public ConfigViewController DetailViewController { get; set; }

        public override void ViewDidLoad ()
        {
            base.ViewDidLoad ();

            var cleanButton = new UIBarButtonItem ("Clean", UIBarButtonItemStyle.Plain, DeleteCheckedItems);
            NavigationItem.RightBarButtonItem = cleanButton;

            EntryField.ShouldEndEditing += (sender) => {
                EntryField.ResignFirstResponder ();
                return true;
            };

            EntryField.EditingDidEndOnExit += AddNewEntry;

            // Custom initialization
            InitializeCouchbaseSummaryView ();
            InitializeDatasource ();

            Datasource.TableView = TableView;
            Datasource.TableView.Delegate = new CouchtableDelegate (this, Datasource);
            TableView.DataSource = Datasource;
            TableView.SectionHeaderHeight = 0;

            UIImage backgroundImage = null;
            switch (Convert.ToInt32 (UIScreen.MainScreen.PreferredMode.Size.Height)) {
            case 480:
                backgroundImage = UIImage.FromBundle ("Default");
                break;
            case 960:
                backgroundImage = UIImage.FromBundle ("Default@2x");
                break;
            default:
                backgroundImage = UIImage.FromBundle ("Default-568h@2x");
                break;
            }

            this.BackgroundImage.Image = backgroundImage;
        }


        View InitializeCouchbaseView ()
        {
            var view = Database.GetView (DefaultViewName);

            var mapBlock = new MapDelegate ((doc, emit) => {
                object date;
                doc.TryGetValue (CreationDatePropertyName, out date);

                object deleted;
                doc.TryGetValue (DeletedKey, out deleted);

                if (date != null && deleted == null)
                    emit (date, doc);
            });

            view.SetMap (mapBlock, "1.1");

            var validationBlock = new ValidateDelegate ((revision, context) => {
                if (revision.IsDeletion)
                    return true;

                object date;
                revision.Properties.TryGetValue (CreationDatePropertyName, out date);
                return (date != null);
            });

            Database.SetValidation (CreationDatePropertyName, validationBlock);

            return view;
        }

        void InitializeCouchbaseSummaryView ()
        {
            var view = Database.GetView ("Done");

            var mapBlock = new MapDelegate ((doc, emit) => {
                object date;
                doc.TryGetValue (CreationDatePropertyName, out date);

                object checkedOff;
                doc.TryGetValue (CheckboxPropertyName, out checkedOff);

                if (date != null) {
                    emit (new [] { checkedOff, date }, null);
                }
            });


            var reduceBlock = new ReduceDelegate ((keys, values, rereduce) => {
                var key = keys.Sum (data =>
                     1 - (int)(((JArray)data) [0])
                );

                var result = new Dictionary<string, string>
                {
                        {"Label", "Items Remaining"},
                        {"Count", key.ToString ()}
                    };

                return result;
            });

            view.SetMapReduce (mapBlock, reduceBlock, "1.1");
        }

        void InitializeDatasource ()
        {

            var view = InitializeCouchbaseView ();

            var query = view.CreateQuery ().ToLiveQuery ();
            query.Descending = true;

            Datasource.Query = query;
            Datasource.LabelProperty = DocumentDisplayPropertyName; // Document property to display in the cell label
            Datasource.Query.Start ();

            var doneView = Database.GetExistingView ("Done") ?? Database.GetView ("Done");
            DoneQuery = doneView.CreateQuery ().ToLiveQuery ();
            DoneQuery.Changed += (sender, e) => {
                var val = default (string);
                if (DoneQuery.Rows.Count == 0) {
                    val = String.Empty;
                } else {
                    var row = DoneQuery.Rows.ElementAt (0);
                    var doc = (IDictionary<string, string>)row.Value;

                    val = String.Format ("{0}: {1}\t", doc ["Label"], doc ["Count"]);
                }
                DoneLabel.Text = val;
            };
            DoneQuery.Start ();
        }
        #endregion
        #region CRUD Operations

        IEnumerable<Document> CheckedDocuments {
            get {
                var docs = new List<Document> ();
                foreach (var row in Datasource.Rows) {
                    var doc = row.Document;
                    object val;

                    if (doc.Properties.TryGetValue (CheckboxPropertyName, out val) && ((bool)val))
                        docs.Add (doc);
                }
                return docs;
            }
        }

        private void AddNewEntry (object sender, EventArgs args)
        {
            var value = EntryField.Text;
            if (String.IsNullOrWhiteSpace (value))
                return;

            var jsonDate = DateTime.UtcNow.ToString ("o"); // ISO 8601 date/time format.
            var vals = new Dictionary<String, Object> {
                {DocumentDisplayPropertyName , value},
                {CheckboxPropertyName , false},
                {CreationDatePropertyName , jsonDate},
                {OwnerKey, ((AppDelegate)UIApplication.SharedApplication.Delegate).Username}
            };

            var doc = Database.CreateDocument ();
            var result = doc.PutProperties (vals);
            if (result == null)
                throw new ApplicationException ("failed to save a new document");


            EntryField.Text = null;
        }

        void DeleteCheckedItems (object sender, EventArgs args)
        {
            var numChecked = CheckedDocuments.Count ();
            if (numChecked == 0)
                return;

            var prompt = String.Format ("Are you sure you want to remove the {0} checked-off item{1}?",
                                        numChecked,
                                        numChecked == 1 ? String.Empty : "s");

            var alert = new UIAlertView ("Remove Completed Items?",
                                         prompt,
                                         null,
                                         "Cancel",
                                         "Remove");

            alert.Dismissed += (alertView, e) => {
                if (e.ButtonIndex == 0)
                    return;

                try {
                    Datasource.DeleteDocuments (CheckedDocuments);
                } catch (Exception ex) {
                    AppDelegate.ShowAlert ("Unabled to delete checked documents", ex, false);
                }
            };
            alert.Show ();
        }
        #endregion

    }
}


