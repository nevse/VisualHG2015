﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Timers;
using System.Windows.Forms;

namespace HgLib
{
    // ---------------------------------------------------------------------------
    // Hg file status cache. The states of the included files are stored in a
    // file status dictionary. Change events are availabe by HgStatusChanged delegate.
    // ---------------------------------------------------------------------------
    public class HgStatus
    {
        public event EventHandler HgStatusChanged;

        HgFileInfoDictionary _fileInfoCache = new HgFileInfoDictionary();

        // directory watcher map - one for each main directory
        DirectoryWatcherMap _directoryWatcherMap = new DirectoryWatcherMap();

        // queued user commands or events from the IDE
        HgCommandQueue _workItemQueue = new HgCommandQueue();

        class RootInfo
        {
            public string Branch;
        };

        // Hg repo root directories - also SubRepo dirs
        Dictionary<string, RootInfo> _rootDirMap = new Dictionary<string, RootInfo>();

        // trigger thread to observe and assimilate the directory watcher changed file dictionaries
        System.Timers.Timer _timerDirectoryStatusChecker;

        // build process is active
        volatile bool _IsSolutionBuilding = false;

        // synchronize to WindowsForms context
        SynchronizationContext _context;

        // Flag to avoid to much rebuild action when .Hg\dirstate was changed.
        // Extenal changes of the dirstate file results definitely in cache rebuild.
        // Changes caused by ourself should not.
        bool _SkipDirstate = false;

        // min elapsed time before cache rebild trigger.
        volatile int _MinElapsedTimeForStatusCacheRebuildMS = 2000;

        // complete cache rebuild is required
        volatile bool _bRebuildStatusCacheRequired = false;

        // ------------------------------------------------------------------------
        // init objects and starts the directory watcher
        // ------------------------------------------------------------------------
        public HgStatus()
        {
            StartDirectoryStatusChecker();
        }

        // ------------------------------------------------------------------------
        // skip / enable dirstate file changes
        // ------------------------------------------------------------------------
        public void SkipDirstate(bool skip)
        {
            _SkipDirstate = skip;
        }

        // ------------------------------------------------------------------------
        // set reset rebuild status flag
        // ------------------------------------------------------------------------
        public bool RebuildStatusCacheRequiredFlag { set { _bRebuildStatusCacheRequired = value; } }

        // ------------------------------------------------------------------------
        // toggle directory watching on / off
        // ------------------------------------------------------------------------
        public void EnableDirectoryWatching(bool enable)
        {
            _directoryWatcherMap.EnableDirectoryWatching(enable);
        }

        // ------------------------------------------------------------------------
        // add one work item to work queue
        // ------------------------------------------------------------------------
        public void AddWorkItem(HgCommand workItem)
        {
            lock (_workItemQueue)
            {
                _workItemQueue.Enqueue(workItem);
            }
        }

        // ------------------------------------------------------------------------
        // GetFileStatus info for the given filename
        // ------------------------------------------------------------------------
        public bool GetFileInfo(string fileName, out HgFileInfo info)
        {
            return _fileInfoCache.TryGetValue(fileName, out info);
        }

        // ------------------------------------------------------------------------
        // Create pending files list
        // ------------------------------------------------------------------------
        public List<HgFileInfo> GetPendingFiles()
        {
            lock (_fileInfoCache)
            {
                return _fileInfoCache.GetPendingFiles();
            }
        }

        // ------------------------------------------------------------------------
        // GetFileStatus for the given filename
        // ------------------------------------------------------------------------
        public HgFileStatus GetFileStatus(string fileName)
        {
            if (_context == null)
                _context = WindowsFormsSynchronizationContext.Current;

            bool found = false;
            HgFileInfo value;

            lock (_fileInfoCache)
            {
                found = _fileInfoCache.TryGetValue(fileName, out value);
            }

            return (found ? value.Status : HgFileStatus.Uncontrolled);
        }

        // ------------------------------------------------------------------------
        // fire status changed event
        // ------------------------------------------------------------------------
        void FireStatusChanged(SynchronizationContext context)
        {
            if (HgStatusChanged != null)
            {
                if (context != null)
                {
                    context.Post(new SendOrPostCallback(x => {
                        HgStatusChanged(this, EventArgs.Empty);
                    }), null);
                }
                else
                {
                    HgStatusChanged(this, EventArgs.Empty);
                }
            }
        }

        public bool AnyItemsUnderSourceControl()
        {
            return (_directoryWatcherMap.Count > 0);
        }

        // ------------------------------------------------------------------------
        // SetCacheDirty triggers a RebuildStatusCache event
        // ------------------------------------------------------------------------
        public void SetCacheDirty()
        {
            _bRebuildStatusCacheRequired = true;
        }

        // ------------------------------------------------------------------------
        /// update given file status.
        // ------------------------------------------------------------------------
        public void UpdateFileStatus(string[] files)
        {
            var fileStatusDictionary = Hg.GetFileStatus(files);
            if (fileStatusDictionary != null)
            {
                _fileInfoCache.Add(fileStatusDictionary);
            }
        }

        // ------------------------------------------------------------------------
        // update given root files status.
        // ------------------------------------------------------------------------
        public void UpdateFileStatus(string root)
        {
            var status = Hg.GetRootStatus(root);

            if (status != null)
            {
                _fileInfoCache.Add(status);
            }
        }

        // ------------------------------------------------------------------------
        /// Add a root directory and query the status of the contining files 
        /// by a QueryRootStatus call.
        // ------------------------------------------------------------------------
        public bool AddRootDirectory(string directory)
        {
            if (directory == string.Empty)
                return false;

            string root = HgProvider.FindRepositoryRoot(directory);
            if (root != string.Empty && !_rootDirMap.ContainsKey(root))
            {
                _rootDirMap[root] = new RootInfo() { Branch = Hg.GetCurrentBranchName(root) };

                if (!_directoryWatcherMap.ContainsDirectory(root))
                {
                    _directoryWatcherMap.WatchDirectory(root);
                }

                var status = Hg.GetRootStatus(root);

                if (status != null)
                {
                    _fileInfoCache.Add(status);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------------
        // get current used brunch of the given roor directory
        // ------------------------------------------------------------------------
        public string GetCurrentBranchOf(string root)
        {
            RootInfo info;

            if (_rootDirMap.TryGetValue(root, out info))
                return info.Branch;

            return string.Empty;

        }

        // ------------------------------------------------------------------------
        // format current brunch
        // ------------------------------------------------------------------------
        public string FormatBranchList()
        {
            string branchList = string.Empty;

            lock (_rootDirMap)
            {
                SortedList<string,int> branches = new SortedList<string, int>();

                //RootInfo info;
                foreach (RootInfo info in _rootDirMap.Values)
                {
                    if (!branches.ContainsKey(info.Branch))
                    {
                        branches.Add(info.Branch, 0);
                        branchList = branchList + (branches.Count > 1 ? ", " : "") + info.Branch;
                    }
                }
            }

            return branchList;
        }

        #region dirstatus changes

        // ------------------------------------------------------------------------
        /// add file to the repositiry if they are not on the ignore list
        // ------------------------------------------------------------------------
        public void AddNotIgnoredFiles(string[] fileListRaw)
        {
            // filter already known files from the list
            List<string> fileList = new List<string>();
            lock (_fileInfoCache)
            {
                foreach (string file in fileListRaw)
                {
                    HgFileInfo info;
                    if (!_fileInfoCache.TryGetValue(file.ToLower(), out info) || info.Status != HgFileStatus.Ignored)
                    {
                        fileList.Add(file);
                    }
                }
            }

            if (fileList.Count == 0)
                return;


            SkipDirstate(true);
            var fileStatusDictionary = Hg.AddFiles(fileList.ToArray());
            if (fileStatusDictionary != null)
            {
                _fileInfoCache.Add(fileStatusDictionary);
            }
            SkipDirstate(false);
        }

        // ------------------------------------------------------------------------
        /// enter file renamed to hg repository
        // ------------------------------------------------------------------------
        public void EnterFileRenamed(string[] oldFileNames, string[] newFileNames)
        {
            var oNameList = new List<string>();
            var nNameList = new List<string>();

            lock (_fileInfoCache)
            {
                for (int pos = 0; pos < oldFileNames.Length; ++pos)
                {
                    string oFileName = oldFileNames[pos];
                    string nFileName = newFileNames[pos];

                    if (nFileName.EndsWith("\\"))
                    {
                        // this is an dictionary - skip it
                    }
                    else if (oFileName.ToLower() != nFileName.ToLower())
                    {
                        oNameList.Add(oFileName);
                        nNameList.Add(nFileName);
                        _fileInfoCache.Remove(oFileName);
                        _fileInfoCache.Remove(nFileName);
                    }
                }
            }

            if (oNameList.Count > 0)
            {
                SkipDirstate(true);
                Hg.EnterFileRenamed(oNameList.ToArray(), nNameList.ToArray());
                SkipDirstate(false);
            }
        }

        // ------------------------------------------------------------------------
        // remove given file from cache
        // ------------------------------------------------------------------------
        public void RemoveFileFromCache(string file)
        {
            lock (_fileInfoCache)
            {
                _fileInfoCache.Remove(file);
            }
        }

        // ------------------------------------------------------------------------
        // file was removed - now update the hg repository
        // ------------------------------------------------------------------------
        public void EnterFilesRemoved(string[] fileList)
        {
            List<string> removedFileList = new List<string>();
            List<string> movedFileList = new List<string>();
            List<string> newNamesList = new List<string>();

            lock (_fileInfoCache)
            {
                foreach (var file in fileList)
                {
                    _fileInfoCache.Remove(file);

                    string newName;
                    if (!_fileInfoCache.FileMoved(file, out newName))
                        removedFileList.Add(file);
                    else
                    {
                        movedFileList.Add(file);
                        newNamesList.Add(newName);
                    }
                }
            }

            if (movedFileList.Count > 0)
                EnterFileRenamed(movedFileList.ToArray(), newNamesList.ToArray());

            if (removedFileList.Count > 0)
            {
                SkipDirstate(true); // avoid a status requery for the repo after hg.dirstate was changed
                var fileStatusDictionary = Hg.EnterFileRemoved(removedFileList.ToArray());
                if (fileStatusDictionary != null)
                {
                    _fileInfoCache.Add(fileStatusDictionary);
                }
                SkipDirstate(false);
            }
        }

        #endregion dirstatus changes

        // ------------------------------------------------------------------------
        // clear the complete cache data
        // ------------------------------------------------------------------------
        public void ClearStatusCache()
        {
            lock (_directoryWatcherMap)
            {
                _directoryWatcherMap.UnsubscribeEvents();
                _directoryWatcherMap.Clear();

            }

            lock (_rootDirMap)
            {
                _rootDirMap.Clear();
            }

            lock (_fileInfoCache)
            {
                _fileInfoCache.Clear();
            }
        }

        // ------------------------------------------------------------------------
        // rebuild the entire _fileStatusDictionary map
        // this includes all files in all watched directories
        // ------------------------------------------------------------------------
        void RebuildStatusCache()
        {
            // remove all status entries
            _fileInfoCache.Clear();

            _bRebuildStatusCacheRequired = false;

            SkipDirstate(true);

            HgFileInfoDictionary newFileStatusDictionary = new HgFileInfoDictionary();
            foreach (var directoryWatcher in _directoryWatcherMap.Watchers)
            {
                // reset the watcher map
                directoryWatcher.DumpDirtyFiles();
            }

            List<string> rootDirList = null;
            lock (_rootDirMap)
            {
                rootDirList = new List<string>(_rootDirMap.Keys);
            }

            // sort dirs by lenght to query from root top to down root
            rootDirList.Sort((a, b) => ((a.Length == b.Length) ? 0 : ((a.Length > b.Length) ? 1 : -1)));
            foreach (string rootDirectory in rootDirList)
            {
                if (rootDirectory != string.Empty)
                {
                    _rootDirMap[rootDirectory].Branch = Hg.GetCurrentBranchName(rootDirectory);

                    var status = Hg.GetRootStatus(rootDirectory);

                    if (status != null)
                    {
                        Trace.WriteLine("RebuildStatusCache - number of files: " + status.Count.ToString());
                        newFileStatusDictionary.Add(status);
                    }
                }
            }

            lock (_fileInfoCache)
            {
                _fileInfoCache = newFileStatusDictionary;
            }

            SkipDirstate(false);
        }

        // ------------------------------------------------------------------------
        // directory watching
        // ------------------------------------------------------------------------
        #region directory watcher

        // ------------------------------------------------------------------------
        // start the trigger thread
        // ------------------------------------------------------------------------
        void StartDirectoryStatusChecker()
        {
            _timerDirectoryStatusChecker = new System.Timers.Timer();
            _timerDirectoryStatusChecker.Elapsed += new ElapsedEventHandler(DirectoryStatusCheckerThread);
            _timerDirectoryStatusChecker.AutoReset = false;
            _timerDirectoryStatusChecker.Interval = 100;
            _timerDirectoryStatusChecker.Enabled = true;
        }

        public void UpdateSolution_StartUpdate()
        {
            _IsSolutionBuilding = true;
        }

        public void UpdateSolution_Done()
        {
            _IsSolutionBuilding = false;
        }

        // ------------------------------------------------------------------------
        // async proc to assimilate the directory watcher state dictionaries
        // ------------------------------------------------------------------------
        void DirectoryStatusCheckerThread(object source, ElapsedEventArgs e)
        {
            // handle user and IDE commands first
            Queue<HgCommand> workItemQueue = _workItemQueue.DumpCommands();
            if (workItemQueue.Count > 0)
            {
                List<string> ditryFilesList = new List<string>();
                foreach (HgCommand item in workItemQueue)
                {
                    item.Run(this, ditryFilesList);
                }

                if (ditryFilesList.Count > 0)
                {
                    var fileStatusDictionary = Hg.GetFileStatus(ditryFilesList.ToArray());
                    if (fileStatusDictionary != null)
                    {
                        lock (_fileInfoCache)
                        {
                            _fileInfoCache.Add(fileStatusDictionary);
                        }
                    }
                }

                // update status icons
                FireStatusChanged(_context);
            }
            else if (!_IsSolutionBuilding)
            {
                // handle modified files list
                long numberOfControlledFiles = 0;
                lock (_fileInfoCache)
                {
                    numberOfControlledFiles = System.Math.Max(1, _fileInfoCache.Count);
                }

                long numberOfChangedFiles = 0;
                double elapsedMS = 0;
                lock (_directoryWatcherMap)
                {
                    numberOfChangedFiles = _directoryWatcherMap.GetNumberOfChangedFiles();
                    TimeSpan timeSpan = new TimeSpan(DateTime.Now.Ticks - _directoryWatcherMap.GetLatestChange().Ticks);
                    elapsedMS = timeSpan.TotalMilliseconds;
                }

                if (_bRebuildStatusCacheRequired || numberOfChangedFiles > 200)
                {
                    if (elapsedMS > _MinElapsedTimeForStatusCacheRebuildMS)
                    {
                        Trace.WriteLine("DoFullStatusUpdate (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        RebuildStatusCache();
                        // update status icons
                        FireStatusChanged(_context);
                    }
                }
                else if (numberOfChangedFiles > 0)
                {
                    // min elapsed time before do anything
                    if (elapsedMS > 2000)
                    {
                        Trace.WriteLine("UpdateDirtyFilesStatus (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        var fileList = PopDirtyWatcherFiles();
                        if (UpdateFileStatusDictionary(fileList))
                        {
                            // update status icons - but only if a project file was changed
                            bool bFireStatusChanged = false;
                            lock (_FileToProjectCache)
                            {
                                foreach (string file in fileList)
                                {
                                    object o;
                                    if (_FileToProjectCache.TryGetValue(file.ToLower(), out o))
                                    {
                                        bFireStatusChanged = true;
                                        break;
                                    }
                                }
                            }

                            if (bFireStatusChanged)
                                FireStatusChanged(_context);
                        }
                    }
                }
            }

            _timerDirectoryStatusChecker.Enabled = true;
        }

        // ------------------------------------------------------------------------
        // Check if the watched file is the hg/dirstate and set _bRebuildStatusCacheRequred to true if required
        // Check if the file state must be refreshed
        // Return: true if the file is dirty, false if not
        // ------------------------------------------------------------------------
        bool PrepareWatchedFile(string fileName)
        {
            bool isDirty = true;

            if (Directory.Exists(fileName))
            {
                // directories are not controlled
                isDirty = false;
            }
            else if (fileName.IndexOf(".hg\\dirstate") > -1)
            {
                if (!_SkipDirstate)
                {
                    _bRebuildStatusCacheRequired = true;
                    Trace.WriteLine(" ... rebuild of status cache required");
                }
                isDirty = false;
            }
            else if (fileName.IndexOf("\\.hg") != -1)
            {
                // all other .hg files are ignored
                isDirty = false;
            }
            else
            {
                HgFileInfo hgFileStatusInfo;

                lock (_fileInfoCache)
                {
                    _fileInfoCache.TryGetValue(fileName, out hgFileStatusInfo);
                }

                if (hgFileStatusInfo != null)
                {
                    FileInfo fileInfo = new FileInfo(fileName);
                    if (fileInfo.Exists)
                    {
                        // see if the file states are equal
                        if ((hgFileStatusInfo.LastWriteTime == fileInfo.LastWriteTime &&
                        hgFileStatusInfo.Length == fileInfo.Length))
                        {
                            isDirty = false;
                        }
                    }
                    else
                    {
                        if (hgFileStatusInfo.Status == HgFileStatus.Removed || hgFileStatusInfo.Status == HgFileStatus.Uncontrolled)
                        {
                            isDirty = false;
                        }
                    }
                }
            }
            return isDirty;
        }

        /// <summary>
        /// list all modified files of all watchers into the return list and reset 
        /// watcher files maps
        /// </summary>
        /// <returns></returns>
        private List<string> PopDirtyWatcherFiles()
        {
            var fileList = new List<string>();
            foreach (var directoryWatcher in _directoryWatcherMap.Watchers)
            {
                var dirtyFiles = directoryWatcher.DumpDirtyFiles();
                if (dirtyFiles.Length > 0)
                {
                    // first collect dirty files list
                    foreach (var dirtyFile in dirtyFiles)
                    {
                        if (PrepareWatchedFile(dirtyFile) && !_bRebuildStatusCacheRequired)
                        {
                            fileList.Add(dirtyFile);
                        }

                        // could be set by PrepareWatchedFile
                        if (_bRebuildStatusCacheRequired)
                            break;
                    }
                }
            }
            return fileList;
        }

        // ------------------------------------------------------------------------
        // update file status of the watched dirty files
        // ------------------------------------------------------------------------
        bool UpdateFileStatusDictionary(List<string> fileList)
        {
            bool updateUI = false;

            if (_bRebuildStatusCacheRequired)
                return false;

            // now we will get Hg status information for the remaining files
            if (!_bRebuildStatusCacheRequired && fileList.Count > 0)
            {
                SkipDirstate(true);
                var fileStatusDictionary = Hg.GetFileStatus(fileList.ToArray());
                if (fileStatusDictionary != null)
                {
                    Trace.WriteLine("got status for watched files - count: " + fileStatusDictionary.Count.ToString());
                    lock (_fileInfoCache)
                    {
                        _fileInfoCache.Add(fileStatusDictionary);
                    }
                }
                SkipDirstate(false);
                updateUI = true;
            }
            return updateUI;
        }

        #endregion directory watcher


        // ------------------------------------------------------------------------
        // files used in any loaded project
        // ------------------------------------------------------------------------
        Dictionary<string, object> _FileToProjectCache = new Dictionary<string, object>();

        public void AddFileToProjectCache(IList<string> fileList, object project)
        {
            lock (_FileToProjectCache)
            {
                foreach (string file in fileList)
                    _FileToProjectCache[file.ToLower()] = project;
            }
        }

        public void RemoveFileFromProjectCache(IList<string> fileList)
        {
            lock (_FileToProjectCache)
            {
                foreach (string file in fileList)
                    _FileToProjectCache.Remove(file.ToLower());
            }
        }

        public void ClearFileToProjectCache()
        {
            lock (_FileToProjectCache)
            {
                _FileToProjectCache.Clear();
            }
        }

        public int FileProjectMapCacheCount()
        {
            return _FileToProjectCache.Count;
        }
    }
}
