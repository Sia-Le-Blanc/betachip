using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 간단한 SORT 기반 트래커 (ID 할당 + IOU 기반 추적)
    /// </summary>
    public class SortTracker
    {
        private int nextId = 0;
        private readonly Dictionary<int, Track> tracks = new();
        private readonly float iouThreshold = 0.3f;
        private readonly int maxAge = 10;

        public class Track
        {
            public int Id;
            public Rect2d Box;
            public int Age = 0;
        }

        /// <summary>
        /// 현재 감지된 BBox 리스트를 기반으로 추적 결과(ID 포함)를 반환
        /// </summary>
        public List<(int id, Rect2d box)> Update(List<Rect2d> detections)
        {
            var results = new List<(int id, Rect2d box)>();
            var unmatchedTracks = new HashSet<int>(tracks.Keys);
            var matched = new HashSet<int>();

            foreach (var det in detections)
            {
                int matchedId = -1;
                double maxIoU = iouThreshold;

                foreach (var track in tracks)
                {
                    if (matched.Contains(track.Key)) continue;

                    double iou = ComputeIoU(track.Value.Box, det);
                    if (iou > maxIoU)
                    {
                        matchedId = track.Key;
                        maxIoU = iou;
                    }
                }

                if (matchedId != -1)
                {
                    tracks[matchedId].Box = det;
                    tracks[matchedId].Age = 0;
                    results.Add((matchedId, det));
                    matched.Add(matchedId);
                    unmatchedTracks.Remove(matchedId);
                }
                else
                {
                    var newTrack = new Track { Id = nextId++, Box = det };
                    tracks[newTrack.Id] = newTrack;
                    results.Add((newTrack.Id, det));
                }
            }

            foreach (var id in unmatchedTracks)
            {
                tracks[id].Age++;
                if (tracks[id].Age > maxAge)
                    tracks.Remove(id);
            }

            return results;
        }

        /// <summary>
        /// IOU 계산 함수
        /// </summary>
        private double ComputeIoU(Rect2d a, Rect2d b)
        {
            double xx1 = Math.Max(a.X, b.X);
            double yy1 = Math.Max(a.Y, b.Y);
            double xx2 = Math.Min(a.X + a.Width, b.X + b.Width);
            double yy2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            double w = Math.Max(0, xx2 - xx1);
            double h = Math.Max(0, yy2 - yy1);
            double inter = w * h;
            double union = a.Width * a.Height + b.Width * b.Height - inter;

            return union <= 0 ? 0 : inter / union;
        }
    }
}
