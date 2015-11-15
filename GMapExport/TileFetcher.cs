﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using log4net;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.MapProviders;
using GMap.NET.Projections;

namespace GMapExport
{
    public class TileFetcher
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(TileFetcher));

        private BackgroundWorker worker = new BackgroundWorker();

        public event EventHandler<TileFetcherEventArgs> PrefetchTileStart;
        public event EventHandler<TileFetcherEventArgs> PrefetchTileComplete;
        public event EventHandler<TileFetcherEventArgs> PrefetchTileProgress;

        private int retry = 3;
        public int Retry
        {
            get { return retry; }
            set { retry = value; }
        }
        private Dictionary<int, List<GPoint>> zoomGPointDic = new Dictionary<int, List<GPoint>>();

        private ulong currentZoomTiles = 0;
        private ulong currentZoomCompleted = 0;
        private int overallProgress = 0;
        private int currentZoom;

        private string tilePath;
        private bool isGenGeoFile = false;

        public TileFetcher()
        {
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;
            worker.ProgressChanged += new ProgressChangedEventHandler(worker_ProgressChanged);
            worker.DoWork += new DoWorkEventHandler(worker_DoWork);
            worker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(worker_RunWorkerCompleted);
        }

        public bool IsBusy
        {
            get { return worker.IsBusy; }
        }

        public void Start(RectLatLng area, int minZoom, int maxZoom, GMapProvider provider, int retryNum)
        {
            this.Retry = retryNum;

            if (!worker.IsBusy)
            {
                WorkerArgs args = new WorkerArgs();
                args.MinZoom = minZoom;
                args.MaxZoom = maxZoom;
                args.Area = area;
                args.Provider = provider;

                GMaps.Instance.UseMemoryCache = false;
                GMaps.Instance.CacheOnIdleRead = false;

                if (PrefetchTileStart != null)
                {
                    PrefetchTileStart(this, new TileFetcherEventArgs(0));
                }

                worker.RunWorkerAsync(args);
            }
        }

        public void Start(RectLatLng area, int minZoom, int maxZoom, GMapProvider provider, string path, int retryNum)
        {
            this.tilePath = path;
            this.Retry = retryNum;

            if (!worker.IsBusy)
            {
                //Export layers for ArcGIS in local disk
                if (path != null)
                {
                    ExportLayerConfig arcgisLayerConfig = new ExportLayerConfig(provider.Projection.Bounds, minZoom, maxZoom, provider, path);
                    if (!arcgisLayerConfig.CreateArcGISMetaFile())
                    {
                        MessageBox.Show("创建ArcGIS图层描述文件失败！");
                    }
                    else
                    {
                        WorkerArgs args = new WorkerArgs();
                        args.MinZoom = minZoom;
                        args.MaxZoom = maxZoom;
                        args.Area = area;
                        args.Provider = provider;

                        GMaps.Instance.UseMemoryCache = false;
                        GMaps.Instance.CacheOnIdleRead = false;

                        worker.RunWorkerAsync(args);
                        if (PrefetchTileStart != null)
                        {
                            PrefetchTileStart(this, new TileFetcherEventArgs(0));
                        }
                    }
                }
            }
        }

        public void Stop()
        {
            if (worker.IsBusy)
            {
                worker.CancelAsync();
            }

            currentZoomTiles = 0;
            currentZoomCompleted = 0;
            overallProgress = 0;
        }

        #region Background Worker

        void worker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                BackgroundWorker worker = (BackgroundWorker)sender;
                WorkerArgs args = (WorkerArgs)e.Argument;

                GMapProvider provider = args.Provider;
                for (int z = args.MinZoom; z <= args.MaxZoom; z++)
                {
                    if (worker.CancellationPending)
                        break;

                    currentZoom = z;
                    currentZoomCompleted = 0;
                    currentZoomTiles = 0;
                    RectLatLng rect = args.Area;
                    int retryCount = 0;

                    GPoint topLeft = provider.Projection.FromPixelToTileXY(provider.Projection.FromLatLngToPixel(rect.LocationTopLeft, z));
                    GPoint rightBottom = provider.Projection.FromPixelToTileXY(provider.Projection.FromLatLngToPixel(rect.LocationRightBottom, z));

                    long begin_x = topLeft.X;
                    long end_x = rightBottom.X;
                    long begin_y = topLeft.Y;
                    long end_y = rightBottom.Y;

                    currentZoomTiles = (ulong)((end_x - begin_x + 1) * (end_y - begin_y + 1));

                    for (long x = begin_x; x <= end_x; ++x)
                    {
                        for (long y = begin_y; y <= end_y; ++y)
                        {
                            if (worker.CancellationPending)
                                break;
                            if (x >= 0 && y >= 0)
                            {
                                GPoint p = new GPoint(x, y);
                                // Download the tile
                                // Retry if there is a failure
                                if (CacheTiles(z, p, provider))
                                    retryCount = 0;
                                else
                                {
                                    if (++retryCount <= retry)
                                    {
                                        y--;
                                        continue;
                                    }
                                    else
                                        retryCount = 0;
                                }
                            }
                            // Report progress
                            currentZoomCompleted++;
                            overallProgress = Convert.ToInt32(currentZoomCompleted * 100 / currentZoomTiles);
                            TileFetcherEventArgs progressArgs = new TileFetcherEventArgs(currentZoomTiles, currentZoomCompleted, currentZoom);
                            worker.ReportProgress(overallProgress, progressArgs);
                        }
                    }
                }

                if (worker.CancellationPending)
                    e.Cancel = true;
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }

        void worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e != null)
            {
                if (PrefetchTileProgress != null)
                {
                    PrefetchTileProgress(this, e.UserState as TileFetcherEventArgs);
                }
            }
        }

        void worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            GMaps.Instance.UseMemoryCache = true;
            GMaps.Instance.CacheOnIdleRead = true;

            if (PrefetchTileComplete != null)
            {
                PrefetchTileComplete(this, new TileFetcherEventArgs(100));
            }
        }

        #endregion

        #region Standalone Methods

        private bool CacheTiles(int zoom, GPoint pos, GMapProvider provider)
        {
            foreach (var pr in provider.Overlays)
            {
                PureImage img;
                try
                {
                    img = pr.GetTileImage(pos, zoom);
                    if (img != null)
                    {
                        // if the tile path is not null, onky write the tile to disk
                        if (tilePath != null)
                        {
                            WriteTileToDisk(img, zoom, pos);
                            if (isGenGeoFile)
                            {
                                WriteGeoFileToDisk(img, zoom, pos);
                            }
                        }
                        else
                        {
                            //write the tile to database
                            GMaps.Instance.PrimaryCache.PutImageToCache(img.Data.ToArray(), pr.DbId, pos, zoom);
                        }
                        img.Dispose();
                        img = null;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    return false;
                }
            }
            return true;
        }

        private void WriteTileToDisk(PureImage img, int zoom, GPoint p)
        {
            DirectoryInfo di = new DirectoryInfo(tilePath + "//Layer//_alllayers");
            string zoomStr = string.Format("{0:D2}", zoom);

            long x = p.X;
            long y = p.Y;
            string col = string.Format("{0:x8}", x).ToLower();
            string row = string.Format("{0:x8}", y).ToLower();

            string dir = di.FullName + "//" + "L" + zoomStr + "//" + "R" + row + "//";
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            FileStream fs = new FileStream(dir + "C" + col + ".png", FileMode.Create, FileAccess.Write);
            BinaryWriter sw = new BinaryWriter(fs);
            //读出图片字节数组至byte[]
            byte[] imageByte = img.Data.ToArray();
            sw.Write(imageByte);
            sw.Close();
            fs.Close();
        }

        private void WriteGeoFileToDisk(PureImage img, int zoom, GPoint p)
        {
            DirectoryInfo dir = new DirectoryInfo(tilePath + "//Layer//_alllayers");
            FileStream fs = new FileStream(dir + "//" + p.Y.ToString() + p.X.ToString() + ".jgw", FileMode.Create, FileAccess.Write);
            StreamWriter sw = new StreamWriter(fs);
            double[] ret = GetCoordinatesFromAddress(p.Y, p.X, zoom);
            sw.WriteLine(ret[0].ToString("0.0000000000"));
            sw.WriteLine("0.0000000000");
            sw.WriteLine("0.0000000000");
            sw.WriteLine(ret[1].ToString("0.0000000000"));
            sw.WriteLine(ret[2].ToString("0.0000000000"));
            sw.WriteLine(ret[3].ToString("0.0000000000"));
            sw.Close();
            fs.Close();
        }

        private double[] GetCoordinatesFromAddress(long row, long col, int zoom)
        {
            MercatorProjection mp = new MercatorProjection();
            double[] ret = new double[4];
            GPoint tmp = mp.FromTileXYToPixel(new GPoint(col, row));
            PointLatLng leftCenter = mp.FromPixelToLatLng(tmp, zoom);

            PointLatLng leftCenter1 = mp.FromPixelToLatLng(new GPoint(tmp.X + 1, tmp.Y), zoom);
            PointLatLng leftCenter2 = mp.FromPixelToLatLng(new GPoint(tmp.X, tmp.Y - 1), zoom);
            ret[0] = leftCenter1.Lng - leftCenter.Lng;
            ret[1] = leftCenter.Lat - leftCenter2.Lat;
            ret[2] = leftCenter.Lng;
            ret[3] = leftCenter.Lat;
            return ret;
        }

        void Shuffle<T>(List<T> deck)
        {
            int N = deck.Count;
            Random random = new System.Random(0);

            for (int i = 0; i < N; ++i)
            {
                int r = i + (int)(random.Next(N - i));
                T t = deck[r];
                deck[r] = deck[i];
                deck[i] = t;
            }
        }
       
        #endregion
    }

    #region Internal Help Class

    internal class WorkerArgs
    {
        public GMapProvider Provider;
        public RectLatLng Area;
        public int MinZoom;
        public int MaxZoom;
    }

    #endregion

}