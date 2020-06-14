using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Data.Json;
using System.Net;
using System.Net.Sockets;
using Windows.Networking.Sockets;
using System.Diagnostics;
using Windows.Storage.Streams;
using System.Text;
using Windows.Storage;
using Windows.System;
using Windows.ApplicationModel.ExtendedExecution.Foreground;
using Windows.ApplicationModel.ExtendedExecution;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace MediaControl
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private CancellationTokenSource cts;
        private CancellationToken ct;
        private Thread bgThread;
        private MediaControlPlaybackStatus playbackStatus = new MediaControlPlaybackStatus();
        private Mutex statusMutex = new Mutex();
        private ExtendedExecutionForegroundSession newSession;
        public MainPage()
        {
            this.InitializeComponent();
            if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Xaml.Media.AcrylicBrush"))
            {
                Windows.UI.Xaml.Media.AcrylicBrush myBrush = new Windows.UI.Xaml.Media.AcrylicBrush();
                myBrush.BackgroundSource = Windows.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop;
                myBrush.TintColor = Color.FromArgb(47, 54, 64, 100);
                myBrush.FallbackColor = Color.FromArgb(47, 54, 64, 100);
                myBrush.TintOpacity = 0.2;
                page.Background = myBrush;
            }
            else
            {
                SolidColorBrush myBrush = new SolidColorBrush(Color.FromArgb(255, 202, 24, 37));
                page.Background = myBrush;
            }
            ApplicationViewTitleBar formattableTitleBar = ApplicationView.GetForCurrentView().TitleBar;
            formattableTitleBar.ButtonBackgroundColor = Colors.Transparent;
            CoreApplicationViewTitleBar coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;
            playButton.Visibility = Visibility.Collapsed;

            newSession = new ExtendedExecutionForegroundSession();
            newSession.Reason = ExtendedExecutionForegroundReason.Unconstrained;
            newSession.Description = "Long Running Processing";
            newSession.Revoked += SessionRevoked;
            newSession.RequestExtensionAsync().Completed = (e, s) =>
            {
                switch (e.GetResults())
                {
                    case ExtendedExecutionForegroundResult.Allowed:
                        Debug.WriteLine("Extended session allowed");
                        break;

                    default:
                    case ExtendedExecutionForegroundResult.Denied:
                        Debug.WriteLine("Extended session denied");
                        break;
                }
            };
            cts = new CancellationTokenSource();
            bgThread = new Thread(new ParameterizedThreadStart(token =>
            {
                CancellationToken ct = (CancellationToken)token;
                while (!ct.IsCancellationRequested)
                {
                    updateTrackInfo();
                    Thread.Sleep(250);
                }
            }));
            bgThread.Start(cts.Token);
            StartServer();
        }

        private void SessionRevoked(object sender, ExtendedExecutionForegroundRevokedEventArgs args)
        {

        }

        ~MainPage()
        {
            listener.Dispose();
            cts.Cancel();
            if (bgThread.IsAlive) bgThread.Join();
        }
        private StreamSocketListener listener;
        private async void StartServer()
        {
            int port = 3000;
            Debug.WriteLine("Starting server");
            listener = new StreamSocketListener();
            listener.ConnectionReceived += (s, e) =>
            {
                ProcessRequestAsync(e.Socket);
            };
            await listener.BindServiceNameAsync(port.ToString());
        }
        private uint BufferSize = 8192;
        private async void ProcessRequestAsync(StreamSocket socket)
        {
            // this works for text only
            StringBuilder request = new StringBuilder();
            using (IInputStream input = socket.InputStream)
            {
                byte[] data = new byte[BufferSize];
                IBuffer buffer = data.AsBuffer();
                uint dataRead = BufferSize;
                while (dataRead == BufferSize)
                {
                    await input.ReadAsync(buffer, BufferSize, InputStreamOptions.Partial);
                    request.Append(Encoding.UTF8.GetString(data, 0, data.Length));
                    dataRead = buffer.Length;
                }
            }
            using (IOutputStream output = socket.OutputStream)
            {
                string requestMethod = request.ToString().Split('\n')[0];
                string[] requestParts = requestMethod.Split(' ');
                string requestType = requestParts[0];
                string requestPath = requestParts.Length > 1 ? requestParts[1] : "/";
                if (requestType == "GET")
                {
                    if (requestPath.ToLower() == "/status")
                        await WritePlaybackStatusJson(output);
                    else if (requestPath.ToLower() == "/thumbnail")
                        await WriteThumbnailAsync(output);
                    else await WriteEmptyResponseAsync(output, 200, "OK");
                }
                else if (requestType == "POST")
                {
                    var session = await getSession();
                    bool success = false;
                    switch (requestPath.ToLower())
                    {
                        case "/play":
                            success = await playTrack(session);
                            break;
                        case "/pause":
                            success = await pauseTrack(session);
                            break;
                        case "/next":
                            success = await nextTrack(session);
                            break;
                        case "/prev":
                            success = await prevTrack(session);
                            break;
                    }
                    if (success)
                        await WriteEmptyResponseAsync(output, 200, "OK");
                }
            }
        }
        private async Task WritePlaybackStatusJson(IOutputStream os)
        {
            Stream resp = os.AsStreamForWrite();
            statusMutex.WaitOne();
            byte[] body = Encoding.UTF8.GetBytes(playbackStatus.Stringify());
            statusMutex.ReleaseMutex();
            byte[] headerArray = Encoding.UTF8.GetBytes(String.Format(
                                          "HTTP/1.1 200 OK\r\n" +
                                          "Content-Length: {0}\r\n" +
                                          "Content-Type: application/json\r\n" +
                                          "Connection: close\r\n\r\n", body.Length));
            await resp.WriteAsync(headerArray, 0, headerArray.Length);
            await resp.WriteAsync(body, 0, body.Length);
            await resp.FlushAsync();
        }
        private async Task WriteEmptyResponseAsync(IOutputStream os, int statusCode, String statusMsg)
        {
            Stream resp = os.AsStreamForWrite();
            byte[] headerArray = Encoding.UTF8.GetBytes(String.Format(
                                          "HTTP/1.1 {0} {1}\r\n" +
                                          "Content-Length:0\r\n" +
                                          "Connection: close\r\n\r\n", statusCode, statusMsg));
            await resp.WriteAsync(headerArray, 0, headerArray.Length);
            await resp.FlushAsync();
        }
        private async Task WriteThumbnailAsync(IOutputStream os)
        {
            Debug.WriteLine("Trying to send thumbnail stream");
            Stream resp = os.AsStreamForWrite();
            statusMutex.WaitOne();
            var thumbnailRef = playbackStatus.thumbnail;
            statusMutex.ReleaseMutex();
            if (thumbnailRef == null) { await WriteEmptyResponseAsync(os, 503, "Service Unavailable"); return; }
            try
            {
                var fs = await thumbnailRef.OpenReadAsync();
                string header = String.Format("HTTP/1.1 200 OK\r\n" +
                                "Content-Length: {0}\r\n" +
                                "Content-Type: image/jpeg\r\n" +
                                "Connection: close\r\n\r\n",
                                fs.Size);
                byte[] headerArray = Encoding.UTF8.GetBytes(header);
                await resp.WriteAsync(headerArray, 0, headerArray.Length);
                await fs.AsStream().CopyToAsync(resp);
                await resp.FlushAsync();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                await WriteEmptyResponseAsync(os, 503, "Service Unavailable"); return;
            }
        }

        public async Task<GlobalSystemMediaTransportControlsSession> getSession()
        {
            try
            {
                return (await GlobalSystemMediaTransportControlsSessionManager.RequestAsync())
                    .GetCurrentSession();
            }
            catch { return null; }
        }
        public async Task<bool> nextTrack(GlobalSystemMediaTransportControlsSession session)
        {
            try { return await session.TrySkipNextAsync(); }
            catch { return false; }
        }
        public async Task<bool> prevTrack(GlobalSystemMediaTransportControlsSession session)
        {
            try { return await session.TrySkipPreviousAsync(); }
            catch { return false; }
        }
        public async Task<bool> pauseTrack(GlobalSystemMediaTransportControlsSession session)
        {
            try { return await session.TryPauseAsync(); }
            catch { return false; }
        }
        public async Task<bool> playTrack(GlobalSystemMediaTransportControlsSession session)
        {
            try { return await session.TryPlayAsync(); }
            catch { return false; }
        }
        public async Task<GlobalSystemMediaTransportControlsSessionMediaProperties> getMediaProperty(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null) return null;
            try { return await session.TryGetMediaPropertiesAsync().AsTask(); }
            catch { return null; }
        }
        public bool isSameTrack(GlobalSystemMediaTransportControlsSessionMediaProperties one, GlobalSystemMediaTransportControlsSessionMediaProperties two)
        {

            return !(one == null || two == null) && (one.Title == two.Title && one.Artist == two.Artist);
        }
        public async void updateTrackInfo(GlobalSystemMediaTransportControlsSession session = null, GlobalSystemMediaTransportControlsSessionMediaProperties currentTrack = null)
        {
            await Task.Run(async () =>
            {
                if (session == null) session = await getSession();
                GlobalSystemMediaTransportControlsSessionMediaProperties nextTrack;
                bool shouldContinue;
                do
                {
                    Thread.Sleep(200);
                    nextTrack = await getMediaProperty(session);
                    shouldContinue = nextTrack == null || isSameTrack(currentTrack, nextTrack);
                } while (shouldContinue);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    var playStatus = session.GetPlaybackInfo().PlaybackStatus;
                    bool isPlaying = playStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    togglePlayButton(isPlaying);
                    var mediaProp = nextTrack;
                    titleLabel.Text = mediaProp.Title;
                    artistLabel.Text = mediaProp.Artist;
                    statusMutex.WaitOne();
                    playbackStatus.title = mediaProp.Title;
                    playbackStatus.artist = mediaProp.Artist;
                    playbackStatus.album = mediaProp.AlbumTitle;
                    playbackStatus.playing = isPlaying;
                    playbackStatus.thumbnail = mediaProp.Thumbnail;
                    statusMutex.ReleaseMutex();
                    if (mediaProp.Thumbnail != null)
                    {
                        try
                        {
                            var thumbnail = new BitmapImage();
                            await thumbnail.SetSourceAsync(await mediaProp.Thumbnail.OpenReadAsync());
                            albumArt.Source = thumbnail;
                            var transform = new ScaleTransform();
                            transform.CenterX = -1;
                            albumArt.RenderTransform = transform;
                        }
                        catch { albumArt.Source = null; }
                    }
                });
            });
        }
        enum PlaybackCommand { Play, Pause, Next, Prev }
        private void togglePlayButton(bool play)
        {
            playButton.Visibility = !play ? Visibility.Visible : Visibility.Collapsed;
            pauseButton.Visibility = play ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void changeTrack(PlaybackCommand command)
        {
            var session = await getSession();
            if (session == null) return;
            var currentTrack = await getMediaProperty(session);
            bool success;
            switch (command)
            {
                case PlaybackCommand.Play:
                    success = await playTrack(session);
                    if (success) togglePlayButton(true);
                    break;
                case PlaybackCommand.Pause:
                    success = await pauseTrack(session);
                    if (success) togglePlayButton(false);
                    break;
                case PlaybackCommand.Next:
                    success = await nextTrack(session);
                    break;
                case PlaybackCommand.Prev:
                    success = await prevTrack(session);
                    break;
            }
            updateTrackInfo(session, currentTrack);
        }
        private void nextButton_Click(object sender, RoutedEventArgs e) => changeTrack(PlaybackCommand.Next);
        private void prevButton_Click(object sender, RoutedEventArgs e) => changeTrack(PlaybackCommand.Prev);
        private void pauseButton_Click(object sender, RoutedEventArgs e) => changeTrack(PlaybackCommand.Pause);
        private void playButton_Click(object sender, RoutedEventArgs e) => changeTrack(PlaybackCommand.Play);
    }
}
