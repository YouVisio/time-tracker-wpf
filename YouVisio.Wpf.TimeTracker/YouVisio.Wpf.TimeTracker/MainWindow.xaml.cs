﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using Newtonsoft.Json;
using YouVisio.Wpf.TimeTracker.Annotations;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace YouVisio.Wpf.TimeTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly LinkedList<TimeSegment> _linkedList = new LinkedList<TimeSegment>();
        private readonly Timer _timer = new Timer(1000);
        private TimeSpan _prevSegment = new TimeSpan(0);

        public MainWindow()
        {
            InitializeComponent();

            _timer.Elapsed += Timer_Elapsed;

            Closing += MainWindow_Closing;

            LoadPreviousLocalStorageData();

            LoadPreviousDatabaseData();

            EnsureTitle();
        }

        void LoadPreviousLocalStorageData()
        {
            var (sprintId, comment) = GetLocalData();
            TaskId.Text = sprintId;
            TaskComment.Text = comment;
        }

        void LoadPreviousDatabaseData()
        {

            var col = GetMongoCollection("time_tracker");

            var day = DateTime.Now;

            _prevSegment = new TimeSpan(0);

            var recordsFromToday = col.FindOne(Query.EQ("day", day.ToYearMonthDay()));
            if (recordsFromToday != null)
            {
                
                _linkedList.Clear();

                var i = 0;
                foreach(BsonDocument seg in recordsFromToday["segments"].AsBsonArray)
                {
                    var ts = GetTimeSegment(day, seg["start"].AsString, seg["end"].AsString);
                    if(seg.Contains("task_id")) ts.Id = seg["task_id"].AsString;
                    if(seg.Contains("task_comment")) ts.Comment = seg["task_comment"].AsString;
                    _prevSegment += ts.Span;
                    ts.Count = ++i;
                    _linkedList.AddLast(ts);
                }
            }

            LoadPreviousDatabaseDataForYesterday();

            var time = _prevSegment;
            LblTime.Content = time.Hours + "h " + time.Minutes + "m " + time.Seconds + "s";

            RecordData(DateTime.Now, _linkedList);
        }

        private TimeSegment GetTimeSegment(DateTime day, string start, string end)
        {
            var sa = start.Split(':').Select(Int32.Parse).ToArray();
            var ea = end.Split(':').Select(Int32.Parse).ToArray();
            return new TimeSegment
                       {
                           Start = new DateTime(day.Year, day.Month, day.Day).AddHours(sa[0]).AddMinutes(sa[1]).AddSeconds(sa[2]),
                           End = new DateTime(day.Year, day.Month, day.Day).AddHours(ea[0]).AddMinutes(ea[1]).AddSeconds(ea[2])
                       };
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_timer.Enabled) Stop();
        }

        private bool EnsureSaveAndIfNeededLastDayClearList()
        {
            var end = _linkedList.Last.Value;
            var lastDayLastPeriodStartString = end.Start.ToYearMonthDay();
            var nowAsString = DateTime.Now.ToYearMonthDay();
            // if we have passed midnight since the beginning of the last time segment
            if (string.CompareOrdinal(lastDayLastPeriodStartString, nowAsString) != 0)
            {
                var endOfYesterday = new DateTime(end.Start.Year, end.Start.Month, end.Start.Day, 23, 59, 59);
                end.End = endOfYesterday;
                RecordData(endOfYesterday, _linkedList);

                _linkedList.Clear();
                _linkedList.AddFirst(new TimeSegment
                {
                    Start = endOfYesterday.AddSeconds(2),
                    End = DateTime.Now,
                    Count = 1,
                    Id = end.Id,
                    Comment = (end.Comment + " (across midnight)").Trim()
                });
                RecordData(DateTime.Now, _linkedList);
                
                return true;
            }
            RecordData(DateTime.Now, _linkedList);
            return false;
        }

        private void RecordData(DateTime dateSaved, LinkedList<TimeSegment> segments)
        {
            var d = dateSaved;
            var node = segments.First;
            var allTime = new TimeSpan(0);
            var doc = new BsonDocument();
            var day = d.Year.ToPadString(4) + "-" + d.Month.ToPadString(2) + "-" + d.Day.ToPadString(2);
            var time = d.Hour.ToPadString(2) + ":" + d.Minute.ToPadString(2);
            doc["day"] = day;
            doc["time"] = time;
            doc["weekday"] = d.DayOfWeek.ToString();
            doc["ts"] = new BsonDateTime(d);
            var timeParts = new BsonArray();
            while (node != null)
            {
                var segment = node.Value;

                timeParts.Add(new BsonDocument
                    {
                        {"start", segment.Start.ToString("HH:mm:ss")},
                        {"end", segment.End.ToString("HH:mm:ss")},
                        {"duration", segment.Span.Hours + "h " + segment.Span.Minutes + "m " + segment.Span.Seconds+"s"},
                        {"minutes", segment.Span.TotalMinutes.Round(2)},
                        {"hours", segment.Span.TotalHours.Round(2)},
                        {"task_id", segment.Id },
                        {"task_comment", segment.Comment }
                    });

                allTime = allTime.Add(segment.Span);
                node = node.Next;
            }

            if (allTime.TotalSeconds < 1)
            {
                if (CanConnectToMongo())
                {
                    var col = GetMongoCollection("time_tracker");
                    col.Remove(Query.EQ("day", day));
                }
                return;
            }
            doc["segments"] = timeParts;
            doc["duration"] = allTime.Hours + "h " + allTime.Minutes + "m " + allTime.Seconds+"s";
            doc["minutes"] = allTime.TotalMinutes.Round(2);
            doc["hours"] = allTime.TotalHours.Round(2);

            if (CanConnectToMongo())
            {
                var col = GetMongoCollection("time_tracker");
                col.Update(Query.EQ("day", day), Update.Replace(doc), UpdateFlags.Upsert);
            }
            else
            {
                var path = Path.GetFullPath(Path.GetTempPath()+ "/__TimeTracker_UnsavedData_" + day + "_" + DateTime.UtcNow.Ticks + ".txt");
                File.WriteAllText(path, doc+"");

                MessageBox.Show("Cannot connect to Mongo. Data saved to "+path);
            }
        }

        private bool CanConnectToMongo()
        {
            var col = GetMongoCollection("test");
            try
            {
                col.Update(Query.EQ("_id", "test"),
                           Update.Replace(new BsonDocument {{"_id", "test"}, {"time", new BsonDateTime(DateTime.Now)}}),
                           UpdateFlags.Upsert);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private MongoCollection<BsonDocument> GetMongoCollection(string name)
        {
            const string cs = "mongodb://localhost/?safe=true";
            var mc = new MongoClient(cs);
            var server = mc.GetServer();
            var db = server.GetDatabase("youvisio");
            return db.GetCollection(name);
        }  

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
                {
                    var time = _prevSegment + (DateTime.Now - _linkedList.Last.Value.Start);

                    TaskbarItemInfo.ProgressValue = Math.Max(Math.Min(time.TotalHours/8.0, 1.0), 0.15);

                    LblTime.Content = time.Hours + "h " + time.Minutes + "m " + time.Seconds + "s";
                });
        }
        void BtnPlay_OnClick(object sender, RoutedEventArgs e)
        {
            if (_timer.Enabled) Stop();
            else Play();
        }

        void Play()
        {
            if (string.IsNullOrWhiteSpace(TaskId.Text) || string.IsNullOrWhiteSpace(TaskComment.Text))
            {
                MessageBox.Show("Please enter sprint ID and Task Comment", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadPreviousDatabaseData();
            _timer.Start();
            BtnPlay.Background = Brushes.DarkGreen;
            BtnPlay.Content = "Stop";
            _linkedList.AddLast(new TimeSegment
            {
                Start = DateTime.Now,
                Count = _linkedList.Count + 1,
                Id = TaskId.Text.Trim().Trim('#'),
                Comment = TaskComment.Text.Trim()
            });
            EnsureTitle();
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
        }

        private void Stop()
        {
            try
            {


                _timer.Stop();
                BtnPlay.Background = Brushes.DarkRed;
                BtnPlay.Content = "Play";
                _linkedList.Last.Value.End = DateTime.Now;

                SaveLocalData(TaskId.Text, TaskComment.Text);
                EnsureSaveAndIfNeededLastDayClearList();
                LoadPreviousDatabaseDataForYesterday();
                EnsureTitle();
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }

        const string fileName = "TimeTracker_SprintAndComment.txt";
        void SaveLocalData(string sprintId, string comment)
        {
            try
            {
                IsolatedStorageFile isoStore = IsolatedStorageFile.GetStore(IsolatedStorageScope.Assembly | IsolatedStorageScope.Machine, null, null);
                if (isoStore.FileExists(fileName))
                {
                    isoStore.DeleteFile(fileName);
                }
                using (IsolatedStorageFileStream isoStream = new IsolatedStorageFileStream(fileName, FileMode.CreateNew, isoStore))
                {
                    using (StreamWriter writer = new StreamWriter(isoStream))
                    {
                        writer.Write(JsonConvert.SerializeObject(new[] { sprintId, comment }));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }
        }
        Version GetPublishedVersion()
        {
            return GetType().Assembly.GetName().Version;
        }

        (string sprintId, string comment) GetLocalData()
        {
            try
            {
                IsolatedStorageFile isoStore = IsolatedStorageFile.GetStore(IsolatedStorageScope.Assembly | IsolatedStorageScope.Machine, null, null);
                if (!isoStore.FileExists(fileName))
                {
                    return ("", "");
                }
                using (IsolatedStorageFileStream isoStream = new IsolatedStorageFileStream(fileName, FileMode.Open, isoStore))
                {
                    using (StreamReader reader = new StreamReader(isoStream))
                    {
                        var data = reader.ReadToEnd();
                        if(string.IsNullOrWhiteSpace(data)) return ("", "");
                        var arr = JsonConvert.DeserializeObject<string[]>(data);
                        return (arr[0], arr[1]);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
                return ("", "");
            }
        }

        void EnsureTitle()
        {
            var d = DateTime.Now;
            var day = d.Year.ToPadString(4) + "-" + d.Month.ToPadString(2) + "-" + d.Day.ToPadString(2) + " "+d.DayOfWeek;
            Title = "Time Tracker @ YouVisio (" + day + ") version ( " + GetPublishedVersion().Revision+" )";
        }

        void LoadPreviousDatabaseDataForYesterday()
        {
            _prevSegment = new TimeSpan(0);
            
            var segments = new List<TimeSegment>();

            var node = _linkedList.Last;
            var i = _linkedList.Count;
            while (node != null)
            {
                var segment = node.Value;
                _prevSegment = _prevSegment.Add(segment.Span);
                segment.Mark = "today";
                segment.Count = i--;
                segments.Add(segment);
                node = node.Previous;
            }

            var day = DateTime.Now.AddDays(-1);
            var col = GetMongoCollection("time_tracker");
            var recordsFromYesterday = col.FindOne(Query.EQ("day", day.ToYearMonthDay()));
            var arr = recordsFromYesterday?["segments"].AsBsonArray;
            if (arr != null)
            {
                const int maxNumRecordsFromYesterday = 100;

                i = arr.Count;
                foreach (BsonDocument seg in
                    arr
                        .Skip(Math.Max(arr.Count - maxNumRecordsFromYesterday, 0))
                        .Take(maxNumRecordsFromYesterday)
                        .Reverse()
                        .OfType<BsonDocument>())
                {
                    var ts = GetTimeSegment(day, seg["start"].AsString, seg["end"].AsString);
                    if (seg.Contains("task_id")) ts.Id = seg["task_id"].AsString;
                    if (seg.Contains("task_comment")) ts.Comment = seg["task_comment"].AsString;
                    ts.Count = i--;
                    ts.Mark = "yesterday";
                    segments.Add(ts);
                }
            }
           
            DataLog.ItemsSource = segments;
        }
        void ClearButton_OnClick(object sender, RoutedEventArgs e)
        {
            TaskId.Text = TaskComment.Text = "";
        }
        void OnSetSegment(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var ts = button.DataContext as TimeSegment;
            TaskId.Text = ts.Id;
            TaskComment.Text = ts.Comment;
        }
        void OnDeleteSegment(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to delete?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if(result != MessageBoxResult.Yes) return;
            var button = sender as Button;
            var ts = button.DataContext as TimeSegment;
            _linkedList.Remove(ts);
            RecordData(DateTime.Now, _linkedList);
            LoadPreviousDatabaseDataForYesterday();
        }
        void OnUpdateSegment(object sender, RoutedEventArgs e)
        {
            RecordData(DateTime.Now, _linkedList);
            LoadPreviousDatabaseDataForYesterday();
        }
    }

    public class TimeSegment : INotifyPropertyChanged
    {
        int _Count;
        DateTime _Start,_End;
        string _Id, _Comment, _Mark;

        public int Count
        {
            get { return _Count; }
            set
            {
                _Count = value;
                OnPropertyChanged();
            }
        }
        public DateTime Start
        {
            get { return _Start; }
            set
            {
                _Start = value;
                OnPropertyChanged();
            }
        }
        public DateTime End
        {
            get { return _End; }
            set
            {
                _End = value;
                OnPropertyChanged();
            }
        }
        public TimeSpan Span
        {
            get
            {
                var s = End - Start;
                return new TimeSpan(s.Days, s.Hours, s.Minutes, s.Seconds);
            }
        }


        public string Id
        {
            get { return _Id; }
            set
            {
                _Id = value;
                OnPropertyChanged();
            }
        }
        public string Comment
        {
            get { return _Comment; }
            set
            {
                _Comment = value;
                OnPropertyChanged();
            }
        }
        public string Mark
        {
            get { return _Mark; }
            set
            {
                _Mark = value;
                OnPropertyChanged();
            }
        }
        

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
