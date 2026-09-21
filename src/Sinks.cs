// IMAPI2 event sinks. These are COM objects *we* implement, so IMAPI2 can call back
// into us while a write is in flight. Each one does nothing but translate the event
// args into BurnState; all rendering happens on the dashboard thread.

using System;
using System.Runtime.InteropServices;

namespace Burnit
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DataWriteSink : IDataWriteEvents
    {
        private readonly BurnState _state;
        public volatile bool CancelRequested;
        public DataAction LastAction = DataAction.ValidatingMedia;

        public DataWriteSink(BurnState state) { _state = state; }

        public void Update(object sender, object progress)
        {
            try
            {
                dynamic p = progress;
                DataAction action = (DataAction)(int)p.CurrentAction;
                LastAction = action;

                int total = 0, lastWritten = 0, startLba = 0;
                try { total = (int)p.SectorCount; } catch (Exception) { }
                try { lastWritten = (int)p.LastWrittenLba; } catch (Exception) { }
                try { startLba = (int)p.StartLba; } catch (Exception) { }

                try { _state.ReportedElapsed = (int)p.ElapsedTime; } catch (Exception) { }
                try { _state.ReportedRemaining = (int)p.RemainingTime; } catch (Exception) { }
                try
                {
                    _state.BufferTotal = (int)p.TotalSystemBuffer;
                    _state.BufferUsed = (int)p.UsedSystemBuffer;
                }
                catch (Exception) { }

                switch (action)
                {
                    case DataAction.ValidatingMedia:
                        _state.Detail = "validating media";
                        _state.Step("validate");
                        _state.Mode = DiscMode.Idle;
                        break;
                    case DataAction.FormattingMedia:
                        _state.Detail = "formatting media";
                        _state.Step("validate");
                        _state.Mode = DiscMode.Idle;
                        break;
                    case DataAction.InitializingHardware:
                        _state.Detail = "initialising drive";
                        _state.Step("calibrate");
                        _state.Mode = DiscMode.Idle;
                        break;
                    case DataAction.CalibratingPower:
                        _state.Detail = "calibrating laser power (OPC)";
                        _state.Step("calibrate");
                        _state.Mode = DiscMode.Idle;
                        break;
                    case DataAction.WritingData:
                        _state.Detail = "writing";
                        _state.Step("write");
                        _state.Mode = DiscMode.Writing;
                        if (total > 0)
                        {
                            long done = (long)(lastWritten - startLba) * 2048L;
                            if (done < 0) done = 0;
                            _state.SetProgress(done, (long)total * 2048L);
                        }
                        break;
                    case DataAction.Finalization:
                        _state.Detail = "closing session";
                        _state.Step("finalise");
                        _state.Mode = DiscMode.Writing;
                        _state.SetProgress(_state.Total, _state.Total);
                        break;
                    case DataAction.Completed:
                        _state.Detail = "complete";
                        _state.Step("finalise");
                        _state.Mode = DiscMode.Done;
                        _state.SetProgress(_state.Total, _state.Total);
                        break;
                    case DataAction.Verifying:
                        _state.Detail = "drive is verifying";
                        _state.Mode = DiscMode.Reading;
                        break;
                }

                if (CancelRequested)
                {
                    try { ((dynamic)sender).CancelWrite(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { /* never let a render detail kill a burn */ }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class TrackAtOnceSink : ITrackAtOnceEvents
    {
        private readonly BurnState _state;
        public volatile bool CancelRequested;

        /// <summary>Sectors already committed by previous tracks, so the bar spans the whole disc.</summary>
        public long SectorBase;
        public long SectorTotal;

        public TrackAtOnceSink(BurnState state) { _state = state; }

        public void Update(object sender, object progress)
        {
            try
            {
                dynamic p = progress;
                TaoAction action = (TaoAction)(int)p.CurrentAction;

                int startLba = 0, lastWritten = 0, track = 0;
                try { startLba = (int)p.StartLba; } catch (Exception) { }
                try { lastWritten = (int)p.LastWrittenLba; } catch (Exception) { }
                try { track = (int)p.CurrentTrackNumber; } catch (Exception) { }
                try { _state.ReportedElapsed = (int)p.ElapsedTime; } catch (Exception) { }
                try { _state.ReportedRemaining = (int)p.RemainingTime; } catch (Exception) { }
                try
                {
                    _state.BufferTotal = (int)p.TotalSystemBuffer;
                    _state.BufferUsed = (int)p.UsedSystemBuffer;
                }
                catch (Exception) { }

                switch (action)
                {
                    case TaoAction.Preparing:
                        _state.Detail = "preparing disc";
                        _state.Step("prepare");
                        _state.Mode = DiscMode.Idle;
                        break;
                    case TaoAction.Writing:
                        _state.Detail = "writing track " + track;
                        _state.Step("write");
                        _state.Mode = DiscMode.Writing;
                        long inTrack = lastWritten - startLba;
                        if (inTrack < 0) inTrack = 0;
                        if (SectorTotal > 0)
                            _state.SetProgress((SectorBase + inTrack) * 2352L, SectorTotal * 2352L);
                        break;
                    case TaoAction.Finishing:
                        _state.Detail = "finalising disc";
                        _state.Step("finalise");
                        _state.Mode = DiscMode.Writing;
                        break;
                    case TaoAction.Verifying:
                        _state.Detail = "drive is verifying";
                        _state.Mode = DiscMode.Reading;
                        break;
                }

                if (CancelRequested)
                {
                    try { ((dynamic)sender).CancelAddTrack(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class EraseSink : IEraseEvents
    {
        private readonly BurnState _state;
        public EraseSink(BurnState state) { _state = state; }

        public void Update(object sender, int elapsedSeconds, int estimatedTotalSeconds)
        {
            try
            {
                _state.Mode = DiscMode.Erasing;
                _state.Detail = "erasing";
                _state.Step("erase");
                _state.ReportedElapsed = elapsedSeconds;
                if (estimatedTotalSeconds > 0)
                {
                    _state.ReportedRemaining = Math.Max(0, estimatedTotalSeconds - elapsedSeconds);
                    // Erase reports time, not bytes; drive the bar off the clock.
                    _state.SetProgress(Math.Min(elapsedSeconds, estimatedTotalSeconds), estimatedTotalSeconds);
                }
            }
            catch (Exception) { }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FsImageSink : IFsImageEvents
    {
        private readonly BurnState _state;
        public FsImageSink(BurnState state) { _state = state; }

        public void Update(object sender, string currentFile, int copiedSectors, int totalSectors)
        {
            try
            {
                _state.Mode = DiscMode.Writing;
                _state.Step("stage");
                if (!string.IsNullOrEmpty(currentFile))
                    _state.Detail = "staging  " + currentFile;
                if (totalSectors > 0)
                    _state.SetProgress((long)copiedSectors * 2048L, (long)totalSectors * 2048L);
            }
            catch (Exception) { }
        }
    }
}
