﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;
using NAudio.Wave;
using PPExtraEventHelper;
using PowerPointLabs.Models;
using PowerPointLabs.AudioMisc;
using PowerPointLabs.Views;
using NAudio;
using PowerPointLabs.XMLMisc;
using Shape = Microsoft.Office.Interop.PowerPoint.Shape;

namespace PowerPointLabs
{
    internal partial class RecorderTaskPane : UserControl
    {
        // map slide id to relative index
        private Dictionary<int, int> _slideRelativeMapper;
        // this offset is used to map a slide id to relative slide id
        private int _relativeSlideCounter;
        // a collection of slides, each slide has a list of audio object
        private List<List<Audio>> _audioList;
        // a collection of slides, each slide has a list of script
        private List<List<string>> _scriptList;
        // a collection of audio buffer, for buffering slide show time recording
        public List<List<Tuple<Audio, int>>> AudioBuffer;
        // a buffer to store the audio that has been replaced
        private Audio _undoAudioBuffer;

        // Records save and display
        private const string SaveNameFormat = "Slide {0} Speech";
        private const string SpeechShapePrefix = "PowerPointLabs Speech";
        private const string SpeechShapePrefixOld = "AudioGen Speech";
        private const string SpeechShapeFormat = "PowerPointLabs Speech {0}";

        private readonly string _tempFolderName;
        private readonly string _tempFullPath;
        private readonly string _tempWaveFileNameFormat;
        private readonly string _tempShapAudioXmlFormat;

        private readonly string _tempPath = Path.GetTempPath();

        private int _recordClipCnt;
        private int _recordTotalLength;

        private enum RecorderStatus
        {
            Idle,
            Recording,
            Playing,
            Pause
        }
        private enum ScriptStatus
        {
            Default,
            Generated,
            Recorded,
            Untracked
        }

        # region Recorder Utilities
        // these utilities wrapped NAudio functions
        private IWaveIn _waveInStream;
        private WaveFileWriter _waveFileWriter;
        private int _currentLength;

        private void WaveInStreamOnDataAvailable(object sender, WaveInEventArgs waveInEventArgs)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new EventHandler<WaveInEventArgs>(WaveInStreamOnDataAvailable), sender, waveInEventArgs);
            }
            else
            {
                if (_waveFileWriter != null)
                {
                    _waveFileWriter.Write(waveInEventArgs.Buffer, 0, waveInEventArgs.BytesRecorded);
                    _currentLength = (int)(_waveFileWriter.Length * 1000 / _waveFileWriter.WaveFormat.AverageBytesPerSecond);
                }
            }
        }

        private void WaveInStreamOnRecordingStopped(object sender, StoppedEventArgs stoppedEventArgs)
        {
            if (_waveFileWriter != null)
            {
                try
                {
                    _waveFileWriter.Dispose();
                    _waveFileWriter = null;
                }
                catch (Exception e)
                {
                    ErrorDialogWrapper.ShowDialog("Error when stopping", "File writing stops with error.", e);
                }
            }
        }

        private void NCleanup()
        {
            try
            {
                _currentLength = 0;

                if (_waveInStream != null)
                {
                    _waveInStream.Dispose();
                    _waveInStream = null;
                }

                if (_waveFileWriter != null)
                {
                    try
                    {
                        _waveFileWriter.Dispose();
                        _waveFileWriter = null;
                    }
                    catch (Exception e)
                    {
                        ErrorDialogWrapper.ShowDialog("Error when stopping", "File writing stops with error.", e);
                        // eat exception locally
                    }
                }
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Error when resource releasing",
                                              "Resources cannot be released successfully.", e);
                throw;
            }
        }

        private bool NInputDeviceExists()
        {
            return WaveIn.DeviceCount > 0;
        }

        private void NStartRecordAudio(string fileName, int rate, int bits, int channel, bool isBackground)
        {
            try
            {
                // prepare wave header and wav output file
                if (isBackground)
                {
                    _waveInStream = new WaveInEvent();
                }
                else
                {
                    _waveInStream = new WaveIn();
                }

                _waveInStream.WaveFormat = new WaveFormat(rate, bits, channel);
                _waveFileWriter = new WaveFileWriter(fileName, _waveInStream.WaveFormat);

                _waveInStream.DataAvailable += WaveInStreamOnDataAvailable;
                //_waveInStream.RecordingStopped += WaveInStreamOnRecordingStopped;

                // start recording here
                _waveInStream.StartRecording();
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Error during recording", "Audio record cannot be started.", e);
                throw;
            }
        }

        private void NStopRecordAudio()
        {
            try
            {
                if (_waveInStream != null)
                {
                    _waveInStream.StopRecording();
                }
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Error when Stopping", "Audio recording stops with error.", e);
                throw;
            }
        }

        private void NMergeAudios(string[] audios, string outputName)
        {
            try
            {
                var buffer = new byte[2048];
                WaveFileWriter writer = null;

                // delete the old file if it exists
                if (File.Exists(outputName))
                {
                    File.Delete(outputName);
                }

                if (audios.Length == 1)
                {
                    if (audios[0] != outputName)
                    {
                        File.Move(audios[0], outputName);
                    }

                    return;
                }

                foreach (var audio in audios)
                {
                    using (var reader = new WaveFileReader(audio))
                    {
                        if (writer == null)
                        {
                            writer = new WaveFileWriter(outputName, reader.WaveFormat);
                        }
                        else
                        {
                            if (!reader.WaveFormat.Equals(writer.WaveFormat))
                            {
                                throw new InvalidOperationException("Can't concatenate WAV Files that don't share the same format");
                            }
                        }

                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, read);
                        }
                    }

                    File.Delete(audio);
                }

                if (writer != null)
                {
                    writer.Dispose();
                }
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Error when Merging", "Audios cannot be merged.", e);
                throw;
            }
        }

        private void NMergeAudios(string path, string baseName, string outputName)
        {
            var audioFiles = Directory.EnumerateFiles(path, "*.wav");
            var audios = audioFiles.Where(audio => audio.Contains(baseName)).ToArray();

            NMergeAudios(audios, outputName);
        }

        private int NGetRecordLengthMillis()
        {
            return _currentLength;
        }
        # endregion

        # region Helper Functions
        private void ResetRecorder()
        {
            soundTrackBar.Value = 0;
            timerLabel.Text = "00:00:00";
            statusLabel.Text = "Ready.";

            recButton.Image = Properties.Resources.Record;
            playButton.Image = Properties.Resources.Play;

            _recButtonStatus = RecorderStatus.Idle;
            _playButtonStatus = RecorderStatus.Idle;
        }

        private void ResetTimer()
        {
            _timerCnt = 0;
            timerLabel.Text = "00:00:00";
            if (_timer != null)
            {
                _timer.Dispose();
            }
        }

        private void ResetTrackbar(int soundBarDefaultPos)
        {
            if (_trackbarThread != null && _trackbarThread.IsAlive)
            {
                _trackbarThread.Interrupt();
            }

            if (_stopwatch != null)
            {
                _stopwatch.Reset();
            }

            soundTrackBar.Value = soundBarDefaultPos;
        }

        private void ResetSession()
        {
            // close unfinished sound session, both from wavin and mci
            AudioHelper.CloseAudio();
            NCleanup();

            // reset timer and trackbar
            ResetTimer();
            ResetTrackbar(0);
        }

        private void SetAllRecorderButtonState(bool enable)
        {
            recButton.Enabled = enable;
            playButton.Enabled = enable;
            stopButton.Enabled = enable;
        }

        private void SetScriptTextBoxScroll()
        {
            // TODO:
            // need to implement
        }

        private int GetRelativeSlideIndex(int curID)
        {
            if (!_slideRelativeMapper.ContainsKey(curID))
            {
                _slideRelativeMapper[curID] = _relativeSlideCounter;

                _relativeSlideCounter++;
            }

            return _slideRelativeMapper[curID];
        }

        private int GetRecordIndexFromScriptIndex(int relativeId, int scriptIndex)
        {
            var recordIndex = -1;

            // if no matched script, return -1 directly
            if (scriptIndex == -1)
            {
                return -1;
            }

            // TODO:
            // this function can be better implemented using binary search

            for (var i = 0; i < _audioList[relativeId].Count; i ++)
            {
                var audio = _audioList[relativeId][i];

                if (audio.MatchScriptID == scriptIndex)
                {
                    recordIndex = i;
                }

                // since the list is sorted according to match script id, if the current
                // matched script ID is larger than script index, we can conclude that
                // there's no mactched record
                if (audio.MatchScriptID > scriptIndex)
                {
                    break;
                }
            }

            return recordIndex;
        }

        private Audio GetPlaybackFromList()
        {
            var slideID = GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID);
            int playbackIndex = -1;
            
            if (recDisplay.SelectedIndices.Count != 0)
            {
                playbackIndex = recDisplay.SelectedIndices[0];
            }
            
            if (playbackIndex == -1)
            {
                return null;
            }
            
            return _audioList[slideID][playbackIndex];
        }

        private Audio GetPlaybackFromList(int scriptIndex, int slideID)
        {
            var relativeSlideID = GetRelativeSlideIndex(slideID);
            int recordIndex = -1;

            if (scriptIndex == -1)
            {
                if (recDisplay.SelectedItems.Count > 0)
                {
                    recordIndex = recDisplay.SelectedIndices[0];
                }
            }
            else
            {
                recordIndex = GetRecordIndexFromScriptIndex(relativeSlideID, scriptIndex);
            }

            if (recordIndex != -1)
            {
                return _audioList[relativeSlideID][recordIndex];
            }

            return null;
        }

        private void UpdateRecordList(int index, string name, string length)
        {
            // change index to 1-base
            index++;
            // add the latest record to the list
            if (index > recDisplay.Items.Count)
            {
                ListViewItem item = recDisplay.Items.Add(index.ToString());
                item.SubItems.Add(name);
                item.SubItems.Add(length);
            }
            else
            {
                // if name needs to be updated
                if (name != null)
                {
                    recDisplay.Items[index - 1].SubItems[1].Text = name;
                }

                // if length needs to be updated
                if (length != null)
                {
                    recDisplay.Items[index - 1].SubItems[2].Text = length;
                }
            }
        }

        private void UpdateRecordList(int relativeSlideID)
        {
            ClearRecordDisplayList();

            for (int index = 0; index < _audioList[relativeSlideID].Count; index ++ )
            {
                var audio = _audioList[relativeSlideID][index];

                ListViewItem item = recDisplay.Items.Add((index + 1).ToString());
                item.SubItems.Add(audio.Name);
                item.SubItems.Add(audio.Length);
            }
        }

        private void UpdateScriptList(int index, string content, ScriptStatus status)
        {
            // change index to 1-base
            index++;

            if (index > scriptDisplay.Items.Count)
            {
                string displayStatus;

                if (status == ScriptStatus.Untracked)
                {
                    displayStatus = "No Audio";
                }
                else
                {
                    displayStatus = status.ToString();
                }

                ListViewItem item = scriptDisplay.Items.Add(displayStatus);
                item.SubItems.Add(content);
            }
            else
            {
                if (status != ScriptStatus.Default)
                {
                    string displayStatus;

                    if (status == ScriptStatus.Untracked)
                    {
                        displayStatus = "No Audio";
                    }
                    else
                    {
                        displayStatus = status.ToString();
                    }

                    scriptDisplay.Items[index - 1].SubItems[0].Text = displayStatus;
                }

                if (content != null)
                {
                    scriptDisplay.Items[index - 1].SubItems[1].Text = content;
                }
            }
        }

        public void UpdateLists(int slideID)
        {
            int relativeID = GetRelativeSlideIndex(slideID);
            List<Audio> audio = _audioList[relativeID];
            List<string> scirpt = _scriptList[relativeID];

            // TODO:
            // Clear all + add all will be very slow, find some means to
            // do it faster

            // update the record list view
            recDisplay.BeginUpdate();
            UpdateRecordList(relativeID);
            recDisplay.EndUpdate();

            // update the script list view
            ClearScriptDisplayList();
            scriptDisplay.BeginUpdate();
            for (int i = 0; i < scirpt.Count; i++)
            {
                var corresRecIndex = GetRecordIndexFromScriptIndex(relativeID, i);

                if (corresRecIndex != -1)
                {
                    if (audio[corresRecIndex].Type == Audio.AudioType.Auto)
                    {
                        UpdateScriptList(i, scirpt[i], ScriptStatus.Generated);
                    }
                    else
                    {
                        UpdateScriptList(i, scirpt[i], ScriptStatus.Recorded);
                    }
                }
                else
                {
                    UpdateScriptList(i, scirpt[i], ScriptStatus.Untracked);
                }
            }
            scriptDisplay.EndUpdate();

            // by default, clear the script detial box
            scriptDetailTextBox.Text = "";

            // since the pane was just renewed, no item is selected thus all
            // button should be disabled
            SetAllRecorderButtonState(false);
        }

        public void UndoLastRecord(int scriptIndex, PowerPointSlide slide)
        {
            int relativeID = GetRelativeSlideIndex(slide.ID);
            int recordIndex = GetRecordIndexFromScriptIndex(relativeID, scriptIndex);

            if (_undoAudioBuffer != null)
            {
                _audioList[relativeID][recordIndex] = _undoAudioBuffer;
            }
            else
            {
                _audioList[relativeID].RemoveAt(recordIndex);
            }
        }

        public void ClearRecordDisplayList()
        {
            recDisplay.BeginUpdate();
            recDisplay.Items.Clear();
            recDisplay.EndUpdate();
        }

        public void ClearScriptDisplayList()
        {
            scriptDisplay.BeginUpdate();
            scriptDisplay.Items.Clear();
            scriptDisplay.EndUpdate();
        }

        public void ClearScriptTextBox()
        {
            scriptDetailTextBox.Text = "";
        }

        public void ClearDisplayLists()
        {
            ClearRecordDisplayList();
            ClearScriptDisplayList();
            ClearScriptTextBox();
        }

        public void ClearRecordDataList()
        {
            // clear the data structure
            foreach (var audioInslide in _audioList)
            {
                audioInslide.Clear();
            }
        }

        public void ClearRecordDataListForSelectedSlides()
        {
            foreach (PowerPointSlide slide in PowerPointPresentation.SelectedSlides)
            {
                ClearRecordDataList(slide.ID);
            }
        }

        public void ClearRecordDataList(int id)
        {
            int relativeIndex = GetRelativeSlideIndex(id);

            // clear data structure
            _audioList[relativeIndex].Clear();
        }

        public void ClearScriptDataList()
        {
            foreach (var slide in _scriptList)
            {
                slide.Clear();
            }
        }

        public void ClearScriptDataList(int id)
        {
            int relativeIndex = GetRelativeSlideIndex(id);
            _scriptList[relativeIndex].Clear();
        }

        public void ClearDataLists()
        {
            ClearRecordDataList();
            ClearScriptDataList();
        }

        public void ClearDataLists(int id)
        {
            ClearRecordDataList(id);
            ClearScriptDataList(id);
        }

        public List<Audio> CopySlideAudio(int slideID)
        {
            var relativeID = GetRelativeSlideIndex(slideID);
            var audioList = new List<Audio>(_audioList[relativeID]);

            return audioList;
        }

        public List<string> CopySlideScript(int slideID)
        {
            var relativeID = GetRelativeSlideIndex(slideID);
            var scriptList = new List<string>(_scriptList[relativeID]);

            return scriptList;
        }

        public void PasteSlideAudio(int slideID, List<Audio> audioList)
        {
            var relativeID = GetRelativeSlideIndex(slideID);

            if (relativeID >= _audioList.Count)
            {
                _audioList.Add(audioList);
            }
            else
            {
                _audioList[relativeID] = audioList;
            }
        }

        public void PasteSlideScript(int slideID, List<string> scriptList)
        {
            var relativeID = GetRelativeSlideIndex(slideID);

            if (relativeID >= _scriptList.Count)
            {
                _scriptList.Add(scriptList);
            }
            else
            {
                _scriptList[relativeID] = scriptList;
            }
        }

        private void DeleteTempAudioFiles()
        {
            var audioFiles = Directory.EnumerateFiles(_tempFullPath, "*.wav");
            var tempAudios = audioFiles.Where(audio => audio.Contains("temp")).ToArray();

            foreach (var audio in tempAudios)
            {
                File.Delete(audio);
            }
        }

        public void DisposeInSlideControlBox()
        {
            if (_inShowControlBox != null)
            {
                _inShowControlBox.Dispose();
            }
        }

        public bool HasEvent()
        {
            return _recButtonStatus != RecorderStatus.Idle || _playButtonStatus != RecorderStatus.Idle;
        }

        public void EnableSlideShow()
        {
            slideShowButton.Enabled = true;
        }

        public void ForceStopEvent()
        {
            if (_recButtonStatus != RecorderStatus.Idle)
            {
                if (_inShowControlBox != null &&
                    _inShowControlBox.GetCurrentStatus() != InShowControl.ButtonStatus.Idle)
                {
                    _inShowControlBox.ForceStop();
                }
                else
                {
                    StopButtonRecordingHandler(_replaceScriptIndex, _replaceScriptSlide, false);
                }
            }

            if (_playButtonStatus != RecorderStatus.Idle)
            {
                StopButtonPlayingHandler();
            }
        }

        public void SetupListsWhenOpen()
        {
            try
            {
                var slides = PowerPointPresentation.Slides.ToList();
                // track the total count of valid speech audio, this helps avoid
                // mixing up other audios with speech audios

                foreach (var slide in slides)
                {
                    // update the slide id to relative id mapper
                    var relativeID = GetRelativeSlideIndex(slide.ID);

                    if (_scriptList.Count == relativeID)
                    {
                        _scriptList.Add(new List<string>());
                    }

                    if (slide.NotesPageText != String.Empty)
                    {
                        // retrieve the tag notes
                        var taggedNotes = new TaggedText(slide.NotesPageText.Trim());
                        List<String> splitScript = taggedNotes.SplitByClicks();

                        // add the splitted notes into script list
                        _scriptList[relativeID] = splitScript;
                    }

                    // mapping the shapes with media files, and set up the audio list

                    // append a new list of of audios to the current presentatoin audio list
                    _audioList.Add(new List<Audio>());

                    // get all audio shapes
                    var shapes = slide.GetShapesWithMediaType(PpMediaType.ppMediaTypeSound);
                    XmlParser xmlParser = null;

                    if (shapes.Count > 0)
                    {
                        xmlParser = new XmlParser(string.Format(_tempShapAudioXmlFormat, slide.Index));
                    }

                    // iterate through all shapes, skip audios that are not generated speech
                    for (int i = 0, speechOnSlide = 0; i < shapes.Count; i++, speechOnSlide++)
                    {
                        var shape = shapes[i];

                        // if current audio is a speech, dump it into Audio object
                        if (shape.Name.Contains(SpeechShapePrefix) ||
                            shape.Name.Contains(SpeechShapePrefixOld))
                        {
                            var audio = new Audio();

                            // detect audio type
                            if (shape.MediaFormat.AudioSamplingRate == Audio.GeneratedSamplingRate)
                            {
                                audio.Type = Audio.AudioType.Auto;
                            }
                            else
                                if (shape.MediaFormat.AudioSamplingRate == Audio.RecordedSamplingRate)
                                {
                                    audio.Type = Audio.AudioType.Record;
                                }
                                else
                                {
                                    MessageBox.Show("Unrecognize Embedded Audio");
                                }

                            // derive matched id from shape name
                            var temp = shape.Name.Split(new[] { ' ' });
                            audio.MatchScriptID = Int32.Parse(temp[2]);

                            // get corresponding audio
                            audio.Name = shape.Name;
                            audio.SaveName = _tempFullPath + xmlParser.GetCorrespondingAudio(audio.Name);
                            audio.Length = AudioHelper.GetAudioLengthString(audio.SaveName);
                            audio.LengthMillis = AudioHelper.GetAudioLength(audio.SaveName);

                            // maintain a sorted audio list
                            // Note: here relativeID == slide.Index - 1
                            if (audio.MatchScriptID >= _audioList[relativeID].Count)
                            {
                                _audioList[relativeID].Add(audio);
                            }
                            else
                            {
                                _audioList[relativeID].Insert(audio.MatchScriptID, audio);
                            }

                            // match id > total script count -> script does not exsit
                            if (audio.MatchScriptID >= _scriptList[relativeID].Count)
                            {
                                audio.MatchScriptID = -1;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Error", "Error during setup", e);
                throw;
            }
        }

        public void InitializeAudioAndScript(PowerPointSlide slide, string[] names, bool forceRefresh)
        {
            string[] audioSaveNames = null;
            string folderPath = _tempPath + _tempFolderName;
            
            int slideID = slide.ID;
            int relativeSlideID = GetRelativeSlideIndex(slideID);
            bool initialized = _audioList != null && _audioList.Count > relativeSlideID;

            // check if the selected slide has been initialized before
            if (initialized)
            {
                // TODO: 
                // if the slide has been initialized, check if the record has been updated

                // currently using forceRefresh to force an entire refresh
                if (!forceRefresh)
                {
                    return;
                }
            }

            // if the script of the selected slide has not been initialized yet,
            // we need to sniff the note pane to initialize the script list

            // TODO:
            // now we assume the first record -> first chunk of note, ect.

            // retrieve the tag notes
            var taggedNotes = new TaggedText(slide.NotesPageText.Trim());
            List<String> splitScript = taggedNotes.SplitByClicks();

            // if the slide has been initialized, update the list
            if (initialized)
            {
                _scriptList[relativeSlideID] = splitScript;
            }
            else
            // add the splitted notes into script list
            {
                _scriptList.Add(splitScript);
            }

            // if the audio of the selected slide has not been initialized yet,
            // we need to put all audio in the current slide into the list.
            if (!initialized)
            {
                _audioList.Add(new List<Audio>());
            }
            // else clear the audio collection of current slide
            // TODO:
            // obviously we don't need to delete all items in the list, only
            // those modified items should be replaced.
            else
            {
                _audioList[relativeSlideID].Clear();
            }

            // if audio names have not been given, retrieve from files.
            if (names == null)
            {
                // retrieve all actual audio files in the slide
                String fileNameSearchPattern = String.Format(SaveNameFormat, slideID);
                
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                var filePaths = Directory.EnumerateFiles(folderPath, "*.wav");
                audioSaveNames = filePaths.Where(path => path.Contains(fileNameSearchPattern)).ToArray();
            }
            else
            {
                audioSaveNames = names;
            }

            // construct audio object and put into audio collection
            for (int i = 0; i < audioSaveNames.Length; i++)
            {
                string saveName = audioSaveNames[i];
                string name = String.Format(SpeechShapeFormat, i);
                var audio = new Audio(name, saveName, i);

                _audioList[relativeSlideID].Add(audio);
            }
        }

        public void InitializeAudioAndScript(List<PowerPointSlide> slides, List<string[]> names, bool forceRefresh)
        {
            // TODO:
            // if a slide has been initialized, check if some of the records have been updated
            // currently use forceRefresh to force an entire refresh

            for (int i = 0; i < slides.Count; i ++)
            {
                var slide = slides[i];

                InitializeAudioAndScript(slide, names[i], forceRefresh);
            }
        }
        # endregion

        # region User Control
        private int _resumeWaitingTime;
        private int _playbackLenMillis;
        private int _timerCnt;
        private int _replaceScriptIndex;
        private PowerPointSlide _replaceScriptSlide;

        private RecorderStatus _recButtonStatus;
        private RecorderStatus _playButtonStatus;

        private System.Threading.Timer _timer;
        private Thread _trackbarThread;

        private Stopwatch _stopwatch;

        private InShowControl _inShowControlBox;

        // delgates to make thread safe control calls
        private delegate void SetLabelTextCallBack(Label label, string text);
        private delegate void SetTrackbarCallBack(TrackBar bar, int pos);
        private delegate void MCISendStringCallBack(string mciCommand,
                                                    StringBuilder mciRetInfo,
                                                    int infoLen,
                                                    IntPtr callBack);

        // call when the pane becomes visible for the first time
        private void RecorderPane_Load(object sender, EventArgs e)
        {
            statusLabel.Text = "Ready.";
            statusLabel.Visible = true;
            ResetRecorder();

            // since this function is called when the pane get loaded for the first time,
            // we should load link the media file and scripts to data structure
            SetupListsWhenOpen();

            // disable all buttons when just enter the pane and nothing has
            // been selected
            SetAllRecorderButtonState(false);

            var currentSlide = PowerPointPresentation.CurrentSlide;
            if (currentSlide != null)
            {
                UpdateLists(currentSlide.ID);
            }
        }

        // call when the pane becomes visible from the second time onwards
        public void RecorderPaneReload()
        {
            statusLabel.Text = "Ready.";
            statusLabel.Visible = true;
            ResetRecorder();

            // disable all buttons when just enter the pane and nothing has
            // been selected
            SetAllRecorderButtonState(false);

            var currentSlide = PowerPointPresentation.CurrentSlide;
            if (currentSlide != null)
            {
                UpdateLists(currentSlide.ID);
            }
        }

        // disable timer and thread when the pane is closed
        public void RecorderPaneClosing()
        {
            // before closing, clean up all unfinished sessions
            AudioHelper.CloseAudio();

            if (_timer != null)
            {
                _timer.Dispose();
            }

            if (_trackbarThread != null && _trackbarThread.IsAlive)
            {
                _trackbarThread.Abort();
            }
        }

        # region Thread Safe Control Methods
        private void ThreadSafeUpdateLabelText(Label label, string time)
        {
            if (label.InvokeRequired)
            {
                SetLabelTextCallBack callback = new SetLabelTextCallBack(ThreadSafeUpdateLabelText);
                Invoke(callback, new object[] { label, time });
            }
            else
            {
                label.Text = time;
            }
        }

        private void ThreadSafeUpdateTrackbarValue(TrackBar bar, int value)
        {
            if (bar.InvokeRequired)
            {
                SetTrackbarCallBack callback = new SetTrackbarCallBack(ThreadSafeUpdateTrackbarValue);
                Invoke(callback, new object[] { bar, value });
            }
            else
            {
                int temp = (int) (value / (double) _playbackLenMillis * bar.Maximum);
                if (temp > bar.Maximum) temp = bar.Maximum;

                bar.Value = temp;
            }
        }

        private void ThreadSafeMCI(string mciCommand,
                                   StringBuilder mciRetInfo,
                                   int infoLen,
                                   IntPtr callBack)
        {
            if (this.InvokeRequired)
            {
                MCISendStringCallBack mciCallBack = new MCISendStringCallBack(ThreadSafeMCI);
                Invoke(mciCallBack, new object[]
                                        {
                                            mciCommand,
                                            mciRetInfo,
                                            infoLen,
                                            callBack
                                        });
            }
            else
            {
                Native.mciSendString(mciCommand,
                              mciRetInfo,
                              infoLen,
                              callBack);
            }
        }
        # endregion

        # region Timer and Trackbar Regualr Event Handlers
        private void TimerEvent(Object o)
        {
            ThreadSafeUpdateLabelText(timerLabel, AudioHelper.ConvertMillisToTime(_timerCnt * 1000));
            _timerCnt++;
        }

        private void TrackbarEvent(Object o)
        {
            if (_stopwatch == null)
            {
                _stopwatch = Stopwatch.StartNew();
            }
            else
            {
                _stopwatch.Start();
            }

            try
            {
                while (true)
                {
                    if (_stopwatch.ElapsedMilliseconds % 5 == 0)
                    {
                        ThreadSafeUpdateTrackbarValue(soundTrackBar, (int)_stopwatch.ElapsedMilliseconds);
                    }
                }
            }
            catch (ThreadInterruptedException interrupt)
            {
            }
        }
        # endregion

        # region Button Event Handlers
        public void RecButtonIdleHandler()
        {
            // close unfinished session
            ResetSession();

            // check input device, abort if no input device connected
            if (!NInputDeviceExists())
            {
                MessageBox.Show("No Input Device suitable for the recording.\n" +
                                "Make sure your computer has a built-in voice picker and has been enabled, " +
                                "or an external voice input device has been connected.", "Input Device Not Found",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            // UI settings
            ResetRecorder();
            statusLabel.Text = "Recording...";
            statusLabel.Visible = true;
            recButton.Image = Properties.Resources.Pause;
            // disable control of playing
            playButton.Enabled = false;
            // enable stop button
            stopButton.Enabled = true;
            // disable control of both lists
            recDisplay.Enabled = false;
            scriptDisplay.Enabled = false;

            // clear the undo buffer
            _undoAudioBuffer = null;

            // track the on going script index if not in slide show mode
            if (_inShowControlBox == null ||
                _inShowControlBox.GetCurrentStatus() == InShowControl.ButtonStatus.Idle)
            {
                // if there's a corresponding script
                if (scriptDisplay.SelectedIndices.Count > 0)
                {
                    _replaceScriptIndex = scriptDisplay.SelectedIndices[0];
                }
                else
                {
                    _replaceScriptIndex = -1;
                }
                
                _replaceScriptSlide = PowerPointPresentation.CurrentSlide;
            }

            // change the status to recording status
            _recButtonStatus = RecorderStatus.Recording;

            // new record, clip counter and total length should be reset
            _recordClipCnt = 0;
            _recordTotalLength = 0;
            // construct new save name
            var tempSaveName = String.Format(_tempWaveFileNameFormat, _recordClipCnt);

            // start recording
            NStartRecordAudio(tempSaveName, 11025, 16, 1, true);

            // start the timer
            _timerCnt = 0;
            _timer = new System.Threading.Timer(TimerEvent, null, 0, 1000);
        }

        private void RecButtonRecordingHandler()
        {
            // make sure stop button is enabled
            stopButton.Enabled = true;

            // change the status to pause and change the button text to resume
            _recButtonStatus = RecorderStatus.Pause;
            statusLabel.Text = "Pause";
            recButton.Image = Properties.Resources.Record;

            // stop the sound, increase clip counter, add current clip length to
            // total record length and stop the timer
            NStopRecordAudio();

            _recordClipCnt++;
            _recordTotalLength += NGetRecordLengthMillis();
            _timer.Dispose();

            // since the timer is counting in seconds, we need to know how many
            // millis to wait before next integral second.

            // retrieve current length
            int currentLen = NGetRecordLengthMillis();
            _resumeWaitingTime = _timerCnt * 1000 - currentLen;

            if (_resumeWaitingTime < 0)
            {
                _resumeWaitingTime = 0;
            }

            NCleanup();
        }

        private void RecButtonPauseHandler()
        {
            // make sure stop button is enabled
            stopButton.Enabled = true;

            // change the status to recording and change the button text to
            // pause
            _recButtonStatus = RecorderStatus.Recording;
            statusLabel.Text = "Recording...";
            recButton.Image = Properties.Resources.Pause;

            // start a new recording, name it after clip counter and restart the timer
            //Native.mciSendString("resume sound", null, 0, IntPtr.Zero);
            var tempSaveName = String.Format(_tempWaveFileNameFormat, _recordClipCnt);
            NStartRecordAudio(tempSaveName, 11025, 16, 1, true);
            _timer = new System.Threading.Timer(TimerEvent, null, _resumeWaitingTime, 1000);
        }

        public void StopButtonRecordingHandler(int scriptIndex, PowerPointSlide currentSlide, bool buffered)
        {
            // enable the control of play button
            playButton.Enabled = true;

            // change rec button status, rec button text, update status label
            // and stop timer
            _recButtonStatus = RecorderStatus.Idle;
            recButton.Image = Properties.Resources.Record;
            statusLabel.Text = "Ready.";
            ResetTimer();

            // get current playback, can be null if there's no matched audio
            var currentPlayback = GetPlaybackFromList(scriptIndex, currentSlide.ID);

            try
            {
                // stop recording in the first play to reduce redundant recording
                NStopRecordAudio();
                
                // adjust the stop time difference between timer-stop and recording-stop
                _recordTotalLength += NGetRecordLengthMillis();
                timerLabel.Text = AudioHelper.ConvertMillisToTime(_recordTotalLength);
                
                // recorder resources clean up
                NCleanup();

                // ask if the user wants to do the replacement
                var result = DialogResult.Yes;

                // prompt to the user only when escaping the slide show while recording
                if (_inShowControlBox != null && 
                    _inShowControlBox.GetCurrentStatus() == InShowControl.ButtonStatus.Estop)
                {
                    if (currentPlayback == null)
                    {
                        result = MessageBox.Show("Do you want to save the record?",
                                                 "Replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    }
                    else
                    {
                        result = MessageBox.Show("Do you want to replace\n" + currentPlayback.Name + "\nwith current record?",
                                                 "Replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    }
                }
                
                if (result == DialogResult.No)
                {
                    // user does not want to save the file, delete all the temp files
                    DeleteTempAudioFiles();
                }
                else
                {
                    // user confirms the recording, save the file and replace the record
                    string saveName;
                    string displayName;
                    Audio newRec = null;

                    var relativeID = GetRelativeSlideIndex(currentSlide.ID);

                    // map the script index with record index
                    // here a simple iteration will find:
                    // 1. the replacement position if a record exists;
                    // 2. an insertion position if a record needs to be added
                    // specially, index == -1 means the record needs to be appended
                    var recordIndex = -1;

                    if (scriptIndex == -1)
                    {
                        if (recDisplay.SelectedItems.Count > 0)
                        {
                            recordIndex = recDisplay.SelectedIndices[0];
                        }
                    }
                    else
                    {
                        for (int i = 0; i < _audioList[relativeID].Count; i++)
                        {
                            var audio = _audioList[relativeID][i];

                            if (audio.MatchScriptID >= scriptIndex)
                            {
                                recordIndex = i;
                                break;
                            }
                        }
                    } 

                    // if current playback != null -> there's a corresponding record for the
                    // script, we can do the replacement;
                    if (currentPlayback != null)
                    {
                        saveName = currentPlayback.SaveName.Replace(".wav", " rec.wav");
                        displayName = currentPlayback.Name;
                        var matchId = currentPlayback.MatchScriptID;
                        
                        if (scriptIndex == -1)
                        {
                            matchId = -1;
                        }
                        
                        newRec = AudioHelper.DumpAudio(displayName, saveName, _recordTotalLength, matchId);

                        // note down the old record and replace the record list
                        _undoAudioBuffer = _audioList[relativeID][recordIndex];
                        _audioList[relativeID][recordIndex] = newRec;

                        // update the item in display
                        // check status of in show control box to:
                        // 1. reduce unnecessary update (won't see the display lists while slide show)
                        // 2. current slide == null during slide show, use in show box status to guard
                        // null ptr exception.
                        if (_inShowControlBox == null ||
                            _inShowControlBox.GetCurrentStatus() != InShowControl.ButtonStatus.Rec &&
                            relativeID == GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID))
                        {
                            UpdateRecordList(recordIndex, displayName, newRec.Length);
                        }
                    }
                    else
                    // if current playback == null -> there's NO corresponding record for the
                    // script, we need to construct the new record and insert it to a proper
                    // position
                    {
                        var saveNameSuffix = " " + scriptIndex.ToString() + " rec.wav";
                        saveName = _tempFullPath + String.Format(SaveNameFormat, relativeID) + saveNameSuffix;
                        
                        // the display name -> which script it corresponds to
                        displayName = String.Format(SpeechShapeFormat, scriptIndex);

                        newRec = AudioHelper.DumpAudio(displayName, saveName, _recordTotalLength, scriptIndex);

                        // insert the new audio
                        if (recordIndex == -1)
                        {
                            _audioList[relativeID].Add(newRec);
                            // update record index, will be used in highlighting
                            recordIndex = _audioList[relativeID].Count - 1;
                        }
                        else
                        {
                            _audioList[relativeID].Insert(recordIndex, newRec);
                        }

                        // update the whole record display list if not in slide show mode
                        if (_inShowControlBox == null ||
                            _inShowControlBox.GetCurrentStatus() != InShowControl.ButtonStatus.Rec &&
                            relativeID == GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID))
                        {
                            UpdateRecordList(relativeID);

                            // highlight the latest added record
                            recDisplay.Items[recordIndex].Selected = true;
                        }
                    }

                    // save current sound -> rename the temp file to the correct save name
                    NMergeAudios(_tempFullPath, "temp", saveName);

                    // update the script list if not in slide show mode
                    if (scriptIndex != -1 && (_inShowControlBox == null ||
                        _inShowControlBox.GetCurrentStatus() != InShowControl.ButtonStatus.Rec &&
                        relativeID == GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID)))
                    {
                        UpdateScriptList(scriptIndex, null, ScriptStatus.Recorded);
                    }

                    // check if we need to buffer the audio or embed the audio
                    if (!buffered)
                    {
                        newRec.EmbedOnSlide(currentSlide, scriptIndex);

                        if (!Globals.ThisAddIn.ribbon.removeAudioEnabled)
                        {
                            Globals.ThisAddIn.ribbon.removeAudioEnabled = true;
                            Globals.ThisAddIn.ribbon.RefreshRibbonControl("removeAudio");
                        }
                    }
                    else
                    {
                        while (AudioBuffer.Count < currentSlide.Index)
                        {
                            AudioBuffer.Add(new List<Tuple<Audio, int>>());
                        }

                        AudioBuffer[currentSlide.Index - 1].Add(new Tuple<Audio, int>(newRec, scriptIndex));
                    }
                }
            }
            catch (Exception e)
            {
                ErrorDialogWrapper.ShowDialog("Record cannot be saved\n",
                                              "Error when saving the file", e);
                throw;
            }
            finally
            // do the following UI re-setup
            {
                // enable control of both lists
                recDisplay.Enabled = true;
                scriptDisplay.Enabled = true;
                // disable stop button
                stopButton.Enabled = false;
            }
        }

        private void StopButtonPlayingHandler()
        {
            // change play button status, update play button text, update
            // status label and reset all sessions
            Native.mciSendString("stop sound", null, 0, IntPtr.Zero);

            // UI settings
            ResetSession();
            _playButtonStatus = RecorderStatus.Idle;
            playButton.Image = Properties.Resources.Play;
            statusLabel.Text = "Ready.";
            // enable both lists
            recDisplay.Enabled = true;
            scriptDisplay.Enabled = true;
            // disable stop button
            stopButton.Enabled = false;
        }

        private void PlayButtonIdleHandler()
        {
            // close unfinished session
            ResetSession();
            ResetRecorder();
            
            // get play back length
            var playback = GetPlaybackFromList();

            if (playback == null)
            {
                MessageBox.Show("No record to play back. Please record first.");
            }
            else
            {
                // UI settings
                statusLabel.Text = "Playing...";
                statusLabel.Visible = true;
                // enable stop button
                stopButton.Enabled = true;
                // disable control of both lists
                recDisplay.Enabled = false;
                scriptDisplay.Enabled = false;

                // change the button status
                _playButtonStatus = RecorderStatus.Playing;
                playButton.Image = Properties.Resources.Pause;

                _playbackLenMillis = playback.LengthMillis;

                // start the timer and track bar
                _timerCnt = 0;
                _timer = new System.Threading.Timer(TimerEvent, null, 0, 1000);
                _trackbarThread = new Thread(TrackbarEvent);
                _trackbarThread.Start();

                // start play back
                AudioHelper.OpenAudio(playback.SaveName);
                Native.mciSendString("play sound notify", null, 0, this.Handle);
            }
        }

        private void PlayButtonPlayingHandler()
        {
            // make sure stop button is enabled
            stopButton.Enabled = true;

            // change the status to pause and change the text to resume
            _playButtonStatus = RecorderStatus.Pause;
            statusLabel.Text = "Pause";
            playButton.Image = Properties.Resources.Play;

            // pause the sound, timer and trackbar
            Native.mciSendString("pause sound", null, 0, IntPtr.Zero);
            _timer.Dispose();
            _stopwatch.Stop();
            _trackbarThread.Interrupt();

            // since the timer is counting in seconds, we need to know how many
            // millis to wait before next integral second.

            // retrieve current length
            int currentLen = AudioHelper.GetAudioCurrentPosition();
            _resumeWaitingTime = _timerCnt * 1000 - currentLen;

            if (_resumeWaitingTime < 0)
            {
                _resumeWaitingTime = 0;
            }
        }

        private void PlayButtonPauseHandler()
        {
            // make sure stop button is enabled
            stopButton.Enabled = true;

            // change the status to playing and change the button text to
            // pause
            _playButtonStatus = RecorderStatus.Playing;
            statusLabel.Text = "Playing...";
            playButton.Image = Properties.Resources.Pause;

            // resume recording, restart the timer and continue the track bar
            Native.mciSendString("resume sound", null, 0, IntPtr.Zero);
            _timer = new System.Threading.Timer(TimerEvent, null, _resumeWaitingTime, 1000);
            _trackbarThread = new Thread(TrackbarEvent);
            _trackbarThread.Start();
        }
        # endregion

        # region UI Control Events
        private void RecButtonClick(object sender, EventArgs e)
        {
            switch (_recButtonStatus)
            {
                case RecorderStatus.Idle:
                    RecButtonIdleHandler();
                    break;
                case RecorderStatus.Recording:
                    RecButtonRecordingHandler();
                    break;
                case RecorderStatus.Pause:
                    RecButtonPauseHandler();
                    break;
                default:
                    MessageBox.Show("Invalid Operation");
                    break;
            }
        }

        private void StopButtonClick(object sender, EventArgs e)
        {
            if (_recButtonStatus == RecorderStatus.Recording ||
                _recButtonStatus == RecorderStatus.Pause)
            {
                StopButtonRecordingHandler(_replaceScriptIndex, _replaceScriptSlide, false);
            } else
            if (_playButtonStatus == RecorderStatus.Playing ||
                _playButtonStatus == RecorderStatus.Pause)
            {
                StopButtonPlayingHandler();
            }
            else
            {
                MessageBox.Show("Invalid Operation");
            }
        }

        private void PlayButtonClick(object sender, EventArgs e)
        {
            switch (_playButtonStatus)
            {
                case RecorderStatus.Idle:
                    PlayButtonIdleHandler();
                    break;
                case RecorderStatus.Playing:
                    PlayButtonPlayingHandler();
                    break;
                case RecorderStatus.Pause:
                    PlayButtonPauseHandler();
                    break;
                default:
                    MessageBox.Show("Invalid Operation");
                    break;
            }
        }

        private void SlideShowButtonClick(object sender, EventArgs e)
        {
            // clear audio buffer
            AudioBuffer.Clear();

            // disable slide show button
            slideShowButton.Enabled = false;

            // get current slide number
            var slideIndex = PowerPointPresentation.CurrentSlide.Index;
            
            // set the starting slide and start the slide show
            var slideShowSettings = Globals.ThisAddIn.Application.ActivePresentation.SlideShowSettings;
            
            // start from the selected slide
            slideShowSettings.StartingSlide = slideIndex;
            slideShowSettings.EndingSlide = PowerPointPresentation.SlideCount;
            slideShowSettings.RangeType = PpSlideShowRangeType.ppShowSlideRange;
            
            // get the slideShowWindow and slideShowView object
            var slideShowWindow = slideShowSettings.Run();

            // unhide the pointer
            slideShowWindow.View.PointerType = PpSlideShowPointerType.ppSlideShowPointerArrow;

            // init the in-show control
            _inShowControlBox = new InShowControl();
            _inShowControlBox.Show();

            // activate the show
            slideShowWindow.Activate();
        }

        private void RecDisplayItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            int relativeSlideID = GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID);
            int corresIndex = _audioList[relativeSlideID][e.ItemIndex].MatchScriptID;

            // if some record is selected, enable the record button
            if (e.IsSelected)
            {
                SetAllRecorderButtonState(true);
                stopButton.Enabled = false;

                if (corresIndex != -1 &&
                    corresIndex < scriptDisplay.Items.Count)
                {
                    scriptDisplay.Items[corresIndex].Selected = true;

                    scriptDetailTextBox.ForeColor = Color.Black;
                    scriptDetailTextBox.Font = new System.Drawing.Font(scriptDetailTextBox.Font, FontStyle.Regular);
                    scriptDetailTextBox.Text = _scriptList[relativeSlideID][corresIndex];
                }
                else
                {
                    scriptDetailTextBox.ForeColor = Color.Red;
                    scriptDetailTextBox.Font = new System.Drawing.Font(scriptDetailTextBox.Font, FontStyle.Bold);
                    scriptDetailTextBox.Text = "Script Not Written";
                }
            }
            else
            {
                // disabling only happens when buttons are idle
                if (_playButtonStatus == RecorderStatus.Idle &&
                    _recButtonStatus == RecorderStatus.Idle)
                {
                    SetAllRecorderButtonState(false);
                }

                if (corresIndex != -1 &&
                    corresIndex < scriptDisplay.Items.Count)
                {
                    scriptDisplay.Items[corresIndex].Selected = false;
                }

                scriptDetailTextBox.Text = "";
            }
        }

        private void ScriptDisplayItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            int relativeSlideID = GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID);
            int corresIndex = GetRecordIndexFromScriptIndex(relativeSlideID, e.ItemIndex);

            if (e.IsSelected)
            {
                SetAllRecorderButtonState(true);
                stopButton.Enabled = false;

                if (corresIndex != -1)
                {
                    recDisplay.Items[corresIndex].Selected = true;
                }
                else
                {
                    playButton.Enabled = false;
                }

                scriptDetailTextBox.Text = _scriptList[relativeSlideID][e.ItemIndex];

                SetScriptTextBoxScroll();
            }
            else
            {
                // disabling only happens when buttons are idle
                if (_playButtonStatus == RecorderStatus.Idle &&
                    _recButtonStatus == RecorderStatus.Idle)
                {
                    SetAllRecorderButtonState(false);
                }

                if (corresIndex != -1)
                {
                    recDisplay.Items[corresIndex].Selected = false;
                }

                scriptDetailTextBox.Text = "";
            }
            
        }

        private void RecDisplayDoubleClick(object sender, EventArgs e)
        {
            // ensure there is and only 1 item has been selected
            if (recDisplay.SelectedItems.Count == 1)
            {
                PlayButtonClick(null, null);
            }
        }

        private void ScriptDisplayDoubleClick(object sender, EventArgs e)
        {
            // ensure there is and only 1 item has been selected
            if (scriptDisplay.SelectedItems.Count == 1)
            {
                var index = scriptDisplay.SelectedIndices[0];
                var relativeID = GetRelativeSlideIndex(PowerPointPresentation.CurrentSlide.ID);
                var recordIndex = GetRecordIndexFromScriptIndex(relativeID, index);
                
                // there is a corresponding record
                if (recordIndex != -1)
                {
                    PlayButtonClick(null, null);
                }
            }
        }

        private void ContextMenuStrip1Opening(object sender, CancelEventArgs e)
        {
            // if user clicks on empty area, the menu will not appear
            if (recDisplay.SelectedItems.Count != 1)
            {
                e.Cancel = true;
            }
        }

        private void ContextMenuStrip1ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            var item = e.ClickedItem;

            if (item.Name.Contains("play"))
            {
                if (recDisplay.SelectedItems.Count == 1)
                {
                    PlayButtonClick(null, null);
                }
            } else
            if (item.Name.Contains("record"))
            {
                if (recDisplay.SelectedItems.Count == 1)
                {
                    RecButtonClick(null, null);
                }
            } else
            if (item.Name.Contains("remove"))
            {
                if (recDisplay.SelectedItems.Count == 1)
                {
                    var currentSlide = PowerPointPresentation.CurrentSlide;
                    var recordIndex = recDisplay.SelectedIndices[0];
                    var relativeSlideID = GetRelativeSlideIndex(currentSlide.ID);
                    var audio = _audioList[relativeSlideID][recordIndex];
                    var scriptIndex = audio.MatchScriptID;

                    // delete the corresponding audio shape
                    currentSlide.DeleteShapesWithPrefix(audio.Name);

                    // delete the item in the data structure
                    _audioList[relativeSlideID].RemoveAt(recordIndex);

                    // update recoder pane
                    UpdateRecordList(relativeSlideID);

                    if (scriptIndex != -1)
                    {
                        UpdateScriptList(scriptIndex, null, ScriptStatus.Untracked);
                    }
                }
            }
        }
        # endregion
        # endregion

        // do when the task pane first initialized
        public RecorderTaskPane(string tempFolderName)
        {
            _audioList = new List<List<Audio>>();
            _scriptList = new List<List<string>>();
            AudioBuffer = new List<List<Tuple<Audio, int>>>();
            
            _slideRelativeMapper = new Dictionary<int, int>();

            _tempFolderName = @"\PowerPointLabs Temp\" + tempFolderName + @"\";
            _tempFullPath = Path.GetTempPath() + _tempFolderName;
            _tempWaveFileNameFormat = _tempFullPath + "temp{0}.wav";
            _tempShapAudioXmlFormat = _tempFullPath + "slide{0}.xml";

            _relativeSlideCounter = 0;
            
            InitializeComponent();

            recButton.Image = Properties.Resources.Record;
            playButton.Image = Properties.Resources.Play;

            scriptDetailTextBox.BackColor = Color.FromKnownColor(KnownColor.Control);

            // don't allow user to touch trackbar, thus disabled
            soundTrackBar.Enabled = false;
        }

        /// <summary>
        /// Overridden Win Form call back function, used to sniff call back
        /// messages triggered by MCI.
        /// </summary>
        /// <param name="m">A reference to the message sent by MCI.</param>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == AudioHelper.MM_MCINOTIFY)
            {
                switch (m.WParam.ToInt32())
                {
                    case AudioHelper.MCI_NOTIFY_SUCCESS:
                        // UI settings
                        statusLabel.Text = "Ready.";
                        playButton.Image = Properties.Resources.Play;
                        _playButtonStatus = RecorderStatus.Idle;
                        // disable stop button
                        stopButton.Enabled = false;
                        // enable both lists
                        recDisplay.Enabled = true;
                        scriptDisplay.Enabled = true;

                        // dispose timer and track bar timer while setting the
                        // track bar to full
                        ResetSession();
                        soundTrackBar.Value = soundTrackBar.Maximum;
                        break;
                    case AudioHelper.MCI_NOTIFY_ABORTED:
                        ResetTrackbar(0);
                        break;
                    default:
                        MessageBox.Show("Fatal error");
                        break;
                }
            }

            base.WndProc(ref m);
        }
    }
}