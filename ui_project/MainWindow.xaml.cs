
namespace ui_project
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Reflection;
    using System.Timers;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using Microsoft.Kinect;
    using Microsoft.Samples.Kinect.SwipeGestureRecognizer;
    using Microsoft.Speech.AudioFormat;
    using Microsoft.Speech.Recognition;

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private KinectSensor sensor;
        private int depthWidth;
        private int depthHeight;
        private const DepthImageFormat DepthFormat = DepthImageFormat.Resolution320x240Fps30;
        private const ColorImageFormat ColorFormat = ColorImageFormat.RgbResolution640x480Fps30;
        private int colorToDepthDivisor;
        private DepthImagePixel[] depthPixels;
        private byte[] colorPixels;
        private int[] greenScreenPixelData;
        private ColorImagePoint[] colorCoordinates;
        private WriteableBitmap colorBitmap;
        private int opaquePixelValue = -1;
        private WriteableBitmap playerOpacityMaskImage = null;

        private List<BitmapImage> BackgroundImages = new List<BitmapImage>();
        private int CurrentBackground = 0;

        private Recognizer recognizer;
        private Skeleton[] skeletons = new Skeleton[0];
        private SpeechRecognitionEngine speechEngine;
        private Dictionary<string, MethodInfo> voiceCommands;
        private bool gesturesInverted = false;

        private Timer slideshowTimer;
        private bool slideshowRunning = false;
        private Timer tagTimer;
        private bool tagShowing = false;
        private int adjustAngle = 5;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow" /> class.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Handles the Loaded event of the Window control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">
        ///   The <see cref="RoutedEventArgs" />
        ///   instance containing the event data.
        /// </param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // load background images
            var curr = Directory.GetCurrentDirectory();
            var images = Directory.GetFiles(curr + "\\Images\\");
            this.slideshowTimer = new Timer(2000);
            this.slideshowTimer.Elapsed += (s, ev) => this.ShowNext();

            foreach (var image in images)
            {
                var bmp = new BitmapImage(new Uri(image));
                this.BackgroundImages.Add(bmp);
            }

            if (this.BackgroundImages.Count > 0)
            {
                var bmp = this.BackgroundImages[0];
                this.imgBackground.Source = bmp;
                this.txtName.Text = Path.GetFileNameWithoutExtension(bmp.UriSource.ToString());
            }

            this.tagTimer = new Timer(500);
            this.tagTimer.Elapsed += (s, ev) =>
            {
                this.InvokeOnUI(
                    () => this.txtTag.Visibility = System.Windows.Visibility.Collapsed);
                this.tagTimer.Stop();
                this.tagShowing = false;
            };

            // Look through all sensors and start the first connected one.
            // This requires that a Kinect is connected at the time of app startup.
            // To make your app robust against plug/unplug, 
            // it is recommended to use KinectSensorChooser provided in Microsoft.Kinect.Toolkit
            foreach (var potentialSensor in KinectSensor.KinectSensors)
            {
                if (potentialSensor.Status == KinectStatus.Connected)
                {
                    this.sensor = potentialSensor;
                    break;
                }
            }

            if (null != this.sensor)
            {
                // Turn on the depth stream to receive depth frames
                this.sensor.DepthStream.Enable(DepthFormat);

                this.depthWidth = this.sensor.DepthStream.FrameWidth;

                this.depthHeight = this.sensor.DepthStream.FrameHeight;

                this.sensor.ColorStream.Enable(ColorFormat);

                int colorWidth = this.sensor.ColorStream.FrameWidth;
                int colorHeight = this.sensor.ColorStream.FrameHeight;

                this.colorToDepthDivisor = colorWidth / this.depthWidth;

                // Turn on to get player masks
                this.sensor.SkeletonStream.Enable();

                // Allocate space to put the depth pixels we'll receive
                this.depthPixels = new DepthImagePixel[this.sensor.DepthStream.FramePixelDataLength];

                // Allocate space to put the color pixels we'll create
                this.colorPixels = new byte[this.sensor.ColorStream.FramePixelDataLength];

                this.greenScreenPixelData = new int[this.sensor.DepthStream.FramePixelDataLength];

                this.colorCoordinates = new ColorImagePoint[this.sensor.DepthStream.FramePixelDataLength];

                // This is the bitmap we'll display on-screen
                this.colorBitmap = new WriteableBitmap(colorWidth, colorHeight, 96.0, 96.0, PixelFormats.Bgr32, null);

                this.imgForeground.Source = this.colorBitmap;

                // Add an event handler to be called whenever there is new depth frame data
                this.sensor.AllFramesReady += this.SensorAllFramesReady;
                this.sensor.SkeletonFrameReady += this.OnSkeletonFrameReady;

                // Start the sensor!
                try
                {
                    this.sensor.Start();
                }
                catch (IOException)
                {
                    this.sensor = null;
                }
            }

            this.recognizer = CreateRecognizer();

            RecognizerInfo ri = GetSpeechRecognizer();
            if (null != ri)
            {
                this.speechEngine = new SpeechRecognitionEngine(ri.Id);
                this.voiceCommands = new Dictionary<string, MethodInfo>();

                var actions = new Choices();
                foreach (var method in typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var attrs = method.GetCustomAttributes(typeof(VoiceCommandAttribute), true);
                    if (attrs.Length > 0)
                    {
                        var attr = attrs[0] as VoiceCommandAttribute;
                        this.voiceCommands.Add(attr.Tag, method);

                        foreach(var item in attr.Items)
                        {
                            actions.Add(new SemanticResultValue(item, attr.Tag));
                        }
                    }
                }
                
                var gb = new GrammarBuilder { Culture = ri.Culture };
                gb.Append(actions);
                
                var g = new Grammar(gb);
                speechEngine.LoadGrammar(g);

                speechEngine.SpeechRecognized += SpeechRecognized;

                speechEngine.SetInputToAudioStream(
                    sensor.AudioSource.Start(), new SpeechAudioFormatInfo(EncodingFormat.Pcm, 16000, 16, 1, 32000, 2, null));
                speechEngine.RecognizeAsync(RecognizeMode.Multiple);
            }
        }

        /// <summary>
        /// Create a wired-up recognizer for running the slideshow.
        /// </summary>
        /// <returns>The wired-up recognizer.</returns>
        private Recognizer CreateRecognizer()
        {
            // Instantiate a recognizer.
            var recognizer = new Recognizer();

            // Wire-up swipe right to manually advance picture.
            recognizer.SwipeRightDetected += (s, e) =>
            {
                if (!gesturesInverted)
                {
                    this.NextBackground();
                }
                else
                {
                    this.PreviousBackground();
                }
            };

            // Wire-up swipe left to manually reverse picture.
            recognizer.SwipeLeftDetected += (s, e) =>
            {
                if (!gesturesInverted)
                {
                    this.PreviousBackground();
                }
                else
                {
                    this.NextBackground();
                }
            };

            return recognizer;
        }

        /// <summary>
        /// Gets the metadata for the speech recognizer (acoustic model) most suitable to
        /// process audio from Kinect device.
        /// </summary>
        /// <returns>
        /// RecognizerInfo if found, <code>null</code> otherwise.
        /// </returns>
        private static RecognizerInfo GetSpeechRecognizer()
        {
            foreach (RecognizerInfo recognizer in SpeechRecognitionEngine.InstalledRecognizers())
            {
                string value;
                recognizer.AdditionalInfo.TryGetValue("Kinect", out value);
                if ("True".Equals(value, StringComparison.OrdinalIgnoreCase) && "en-US".Equals(recognizer.Culture.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return recognizer;
                }
            }

            return null;
        }
        
        /// <summary>
        /// Handler for recognized speech events.
        /// </summary>
        /// <param name="sender">object sending the event.</param>
        /// <param name="e">event arguments.</param>
        private void SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            // Speech utterance confidence below which we treat speech as if it hadn't been heard
            const double ConfidenceThreshold = 0.3;

            if (e.Result.Confidence >= ConfidenceThreshold)
            {
                var tag = e.Result.Semantics.Value.ToString();
                this.voiceCommands[tag].Invoke(this, null);
            }
        }

        /// <summary>
        /// Takes a picture.
        /// </summary>
        [VoiceCommand("CAPTURE", "screen shot", "take picture")]
        private void TakePicture()
        {
            if (null == this.sensor)
            {
                return;
            }

            int colorWidth = this.sensor.ColorStream.FrameWidth;
            int colorHeight = this.sensor.ColorStream.FrameHeight;

            // create a render target that we'll render our controls to
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(colorWidth, colorHeight, 96.0, 96.0, PixelFormats.Pbgra32);

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                // render the backdrop
                VisualBrush backdropBrush = new VisualBrush(imgBackground);
                dc.DrawRectangle(backdropBrush, null, new Rect(new Point(), new Size(colorWidth, colorHeight)));

                // render the color image masked out by players
                VisualBrush colorBrush = new VisualBrush(imgForeground);
                dc.DrawRectangle(colorBrush, null, new Rect(new Point(), new Size(colorWidth, colorHeight)));
            }

            renderBitmap.Render(dv);
            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            string time = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string folder = Directory.GetCurrentDirectory();
            string path = Path.Combine(folder, "ScreenShot_" + time + ".png");

            // write the new file to disk
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(fs);
                }

                this.pnlCaptures.Children.Add(new System.Windows.Controls.Image() { Source = renderBitmap, Margin = new Thickness(5, 5, 5, 0) });
                this.pnlCaptures.Children.Add(new System.Windows.Controls.TextBlock() { Text = Path.GetFileNameWithoutExtension(path), Margin = new Thickness(0, 0, 0, 5), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
                this.ShowTag("TAKING PICTURE");
            }
            catch (IOException)
            {
                this.ShowTag("ERROR TAKING PICTURE");
            }
        }

        /// <summary>
        /// Clears the picture.
        /// </summary>
        [VoiceCommand("CLEAR", "clear pictures")]
        private void ClearPicture()
        {
            this.ShowTag("CLEARING PICTURES");
            this.pnlCaptures.Children.Clear();
        }

        /// <summary>
        /// Starts the slide show.
        /// </summary>
        [VoiceCommand("START", "start")]
        private void StartSlideShow()
        {
            if (!slideshowRunning)
            {
                this.slideshowTimer.Start();
                this.slideshowRunning = true;
                this.ShowTag("START SLIDESHOW");
            }
        }

        /// <summary>
        /// Stops the slide show.
        /// </summary>
        [VoiceCommand("STOP", "stop")]
        private void StopSlideShow()
        {
            if (slideshowRunning)
            {
                this.slideshowTimer.Stop();
                this.slideshowRunning = false;
                this.ShowTag("STOP SLIDESHOW");
            }
        }

        /// <summary>
        /// Nexts the background.
        /// </summary>
        [VoiceCommand("NEXT", "next")]
        private void NextBackground()
        {
            if (!slideshowRunning)
            {
                this.ShowTag("NEXT");
                ShowNext();
            }
        }

        /// <summary>
        /// Previouses the background.
        /// </summary>
        [VoiceCommand("PREVIOUS", "previous")]
        private void PreviousBackground()
        {
            if (!slideshowRunning)
            {
                this.ShowTag("PREVIOUS");
                ShowPrevious();
            }
        }

        /// <summary>
        /// Tilts the kinect sensor up.
        /// </summary>
        [VoiceCommand("UP", "look up", "tilt up")]
        private void TiltUp()
        {
            var curr = this.sensor.ElevationAngle;
            curr += this.adjustAngle;
            if (curr > 27)
            {
                curr = 27;
            }

            this.ShowTag("CAMERA ANGLE UP");
            var bg = new BackgroundWorker();
            bg.DoWork += (s, e) => this.sensor.ElevationAngle = curr;
            bg.RunWorkerAsync();
        }

        /// <summary>
        /// Tilts the kinect sensor down.
        /// </summary>
        [VoiceCommand("DOWN", "look down", "tilt down")]
        private void TiltDown()
        {
            var curr = this.sensor.ElevationAngle;
            curr -= this.adjustAngle;
            if (curr < -27)
            {
                curr = -27;
            }

            this.ShowTag("CAMERA ANGLE DOWN");
            var bg = new BackgroundWorker();
            bg.DoWork += (s, e) => this.sensor.ElevationAngle = curr;
            bg.RunWorkerAsync();
        }

        /// <summary>
        /// Shows the help.
        /// </summary>
        [VoiceCommand("SHOW_HELP", "show help")]
        private void ShowHelp()
        {
            //pnlCaptures.Visibility = System.Windows.Visibility.Collapsed;
            txtShowHelp.Visibility = System.Windows.Visibility.Collapsed;

            pnlHelp.Visibility = System.Windows.Visibility.Visible;
            txtHideHelp.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// Hides the help.
        /// </summary>
        [VoiceCommand("HIDE_HELP", "hide help")]
        private void HideHelp()
        {
            //pnlCaptures.Visibility = System.Windows.Visibility.Visible;
            txtShowHelp.Visibility = System.Windows.Visibility.Visible;

            pnlHelp.Visibility = System.Windows.Visibility.Collapsed;
            txtHideHelp.Visibility = System.Windows.Visibility.Collapsed;
        }

        /// <summary>
        /// Shows the name of the file.
        /// </summary>
        [VoiceCommand("SHOW_FILENAME", "show name", "show file name")]
        private void ShowFileName()
        {
            this.InvokeOnUI(() => this.txtName.Visibility = System.Windows.Visibility.Visible);
        }

        /// <summary>
        /// Hides the name of the file.
        /// </summary>
        [VoiceCommand("HIDE_FILENAME", "hide name", "hide file name")]
        private void HideFileName()
        {
            this.InvokeOnUI(() => this.txtName.Visibility = System.Windows.Visibility.Collapsed);
        }

        /// <summary>
        /// Inverts the gestures.
        /// </summary>
        [VoiceCommand("INVERT", "invert gestures")]
        private void InvertGestures()
        {
            this.gesturesInverted = !this.gesturesInverted;

            if (this.gesturesInverted)
            {
                this.txtInverted.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                this.txtInverted.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Exits the app.
        /// </summary>
        [VoiceCommand("EXIT", "exit program")]
        private void Exit()
        {
            this.Close();
        }

        /// <summary>
        /// Shows the next background image.
        /// </summary>
        private void ShowNext()
        {
            this.CurrentBackground = (this.CurrentBackground + 1) % this.BackgroundImages.Count;

            var bmp = this.BackgroundImages[this.CurrentBackground];
            this.InvokeOnUI(
                () =>
                {
                    this.imgBackground.Source = bmp;
                    this.txtName.Text = Path.GetFileNameWithoutExtension(bmp.UriSource.ToString());
                });
        }

        /// <summary>
        /// Shows the previous background image.
        /// </summary>
        private void ShowPrevious()
        {
            this.CurrentBackground--;

            if (this.CurrentBackground < 0)
            {
                this.CurrentBackground += this.BackgroundImages.Count;
            }

            var bmp = this.BackgroundImages[this.CurrentBackground];
            this.InvokeOnUI(
                () =>
                {
                    this.imgBackground.Source = bmp;
                    this.txtName.Text = Path.GetFileNameWithoutExtension(bmp.UriSource.ToString());
                });
        }

        /// <summary>
        /// Invokes an action the on the UI thread.
        /// </summary>
        /// <param name="action">The action.</param>
        private void InvokeOnUI(Action action)
        {
            this.Dispatcher.Invoke(action);
        }


        /// <summary>
        /// Shows the tag.
        /// </summary>
        /// <param name="tag">The tag.</param>
        private void ShowTag(string tag)
        {
            if (this.tagShowing)
            {
                this.tagTimer.Stop();
            }

            this.InvokeOnUI(
                () =>
                {
                    this.txtTag.Text = tag;
                    this.txtTag.Visibility = System.Windows.Visibility.Visible;
                });

            this.tagTimer.Start();
        }

        /// <summary>
        /// Handler for skeleton ready handler.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The event args.</param>
        private void OnSkeletonFrameReady(object sender, SkeletonFrameReadyEventArgs e)
        {
            // Get the frame.
            using (var frame = e.OpenSkeletonFrame())
            {
                // Ensure we have a frame.
                if (frame != null)
                {
                    // Resize the skeletons array if a new size (normally only on first call).
                    if (this.skeletons.Length != frame.SkeletonArrayLength)
                    {
                        this.skeletons = new Skeleton[frame.SkeletonArrayLength];
                    }

                    // Get the skeletons.
                    frame.CopySkeletonDataTo(this.skeletons);

                    this.recognizer.Recognize(sender, frame, this.skeletons);
                }
            }
        }

        /// <summary>
        /// Event handler for Kinect sensor's DepthFrameReady event
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void SensorAllFramesReady(object sender, AllFramesReadyEventArgs e)
        {
            // in the middle of shutting down, so nothing to do
            if (null == this.sensor)
            {
                return;
            }

            bool depthReceived = false;
            bool colorReceived = false;

            using (DepthImageFrame depthFrame = e.OpenDepthImageFrame())
            {
                if (null != depthFrame)
                {
                    // Copy the pixel data from the image to a temporary array
                    depthFrame.CopyDepthImagePixelDataTo(this.depthPixels);

                    depthReceived = true;
                }
            }

            using (ColorImageFrame colorFrame = e.OpenColorImageFrame())
            {
                if (null != colorFrame)
                {
                    // Copy the pixel data from the image to a temporary array
                    colorFrame.CopyPixelDataTo(this.colorPixels);

                    colorReceived = true;
                }
            }

            // do our processing outside of the using block
            // so that we return resources to the kinect as soon as possible
            if (true == depthReceived)
            {
                this.sensor.CoordinateMapper.MapDepthFrameToColorFrame(
                    DepthFormat,
                    this.depthPixels,
                    ColorFormat,
                    this.colorCoordinates);

                Array.Clear(this.greenScreenPixelData, 0, this.greenScreenPixelData.Length);

                // loop over each row and column of the depth
                for (int y = 0; y < this.depthHeight; ++y)
                {
                    for (int x = 0; x < this.depthWidth; ++x)
                    {
                        // calculate index into depth array
                        int depthIndex = x + (y * this.depthWidth);

                        DepthImagePixel depthPixel = this.depthPixels[depthIndex];

                        int player = depthPixel.PlayerIndex;

                        // if we're tracking a player for the current pixel, do green screen
                        if (player > 0)
                        {
                            // retrieve the depth to color mapping for the current depth pixel
                            ColorImagePoint colorImagePoint = this.colorCoordinates[depthIndex];

                            // scale color coordinates to depth resolution
                            int colorInDepthX = colorImagePoint.X / this.colorToDepthDivisor;
                            int colorInDepthY = colorImagePoint.Y / this.colorToDepthDivisor;

                            // make sure the depth pixel maps to a valid point in color space
                            // check y > 0 and y < depthHeight to make sure we don't write outside of the array
                            // check x > 0 instead of >= 0 since to fill gaps we set opaque current pixel plus the one to the left
                            // because of how the sensor works it is more correct to do it this way than to set to the right
                            if (colorInDepthX > 0 && colorInDepthX < this.depthWidth && colorInDepthY >= 0 && colorInDepthY < this.depthHeight)
                            {
                                // calculate index into the green screen pixel array
                                int greenScreenIndex = colorInDepthX + (colorInDepthY * this.depthWidth);

                                // set opaque
                                this.greenScreenPixelData[greenScreenIndex] = opaquePixelValue;

                                // compensate for depth/color not corresponding exactly by setting the pixel 
                                // to the left to opaque as well
                                this.greenScreenPixelData[greenScreenIndex - 1] = opaquePixelValue;
                            }
                        }
                    }
                }
            }

            // do our processing outside of the using block
            // so that we return resources to the kinect as soon as possible
            if (true == colorReceived)
            {
                // Write the pixel data into our bitmap
                this.colorBitmap.WritePixels(
                    new Int32Rect(0, 0, this.colorBitmap.PixelWidth, this.colorBitmap.PixelHeight),
                    this.colorPixels,
                    this.colorBitmap.PixelWidth * sizeof(int),
                    0);

                if (this.playerOpacityMaskImage == null)
                {
                    this.playerOpacityMaskImage = new WriteableBitmap(
                        this.depthWidth,
                        this.depthHeight,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null);

                    imgForeground.OpacityMask = new ImageBrush { ImageSource = this.playerOpacityMaskImage };
                }

                this.playerOpacityMaskImage.WritePixels(
                    new Int32Rect(0, 0, this.depthWidth, this.depthHeight),
                    this.greenScreenPixelData,
                    this.depthWidth * ((this.playerOpacityMaskImage.Format.BitsPerPixel + 7) / 8),
                    0);
            }
        }
    }
}
