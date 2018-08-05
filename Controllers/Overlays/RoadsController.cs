﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Drawing;
using SixLabors.ImageSharp.Processing.Drawing.Pens;
using SixLabors.Primitives;

namespace osm_road_overlay.Controllers.Overlays
{
    [Route("overlays/roads")]
    public class RoadsController : Controller
    {
        const string OverpassAPIEndpoint = "https://overpass-api.de/api/interpreter";
        const float LaneWidthCycle = 0.333f;

        struct LatLon {
            public double Lat;
            public double Lon;
        }

        static LatLon GetLatLonFromTile(int zoom, int x, int y) {
            var n = Math.Pow(2, zoom);
            return new LatLon {
                Lat = 180 / Math.PI * Math.Atan(Math.Sinh(Math.PI - (2 * Math.PI * y / n))),
                Lon = (x / n * 360) - 180,
            };
        }

        static void GetLatLonBoxFromTile(int zoom, int x, int y, out LatLon nw, out LatLon se)
        {
            nw = GetLatLonFromTile(zoom, x, y);
            se = GetLatLonFromTile(zoom, x + 1, y + 1);
        }

        static string GetBoundingBoxFromLatLonBox(LatLon nw, LatLon se, double oversize)
        {
            var latExtra = oversize * (nw.Lat - se.Lat);
            var lonExtra = oversize * (se.Lon - nw.Lon);
            return $"{se.Lat - latExtra},{nw.Lon - lonExtra},{nw.Lat + latExtra},{se.Lon + lonExtra}";
        }

        static PointF GetPointFromNode(LatLon nw, LatLon se, OverpassResponseElement node) {
            return new PointF(
                (float)(256 * (node.lon - nw.Lon) / (se.Lon - nw.Lon)),
                (float)(256 * (node.lat - nw.Lat) / (se.Lat - nw.Lat))
            );
        }

        static SizeF Vector(PointF start, PointF end) {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            return new SizeF() {
                Width = (float)(dx / length),
                Height = (float)(dy / length)
            };
        }

        static SizeF Flip(SizeF point) {
            return new SizeF() {
                Width = -point.Height,
                Height = point.Width
            };
        }

        static PointF Offset(PointF point, SizeF direction, float distance) {
            return new PointF() {
                X = point.X + direction.Width * distance,
                Y = point.Y + direction.Height * distance,
            };
        }

        struct OverpassResponse {
            public OverpassResponseElement[] elements;
        }

        struct OverpassResponseElement {
            public string type;
            public long id;
            public double lat;
            public double lon;
            public long[] nodes;
            public Dictionary<string, string> tags;
        }

        readonly HttpClient Client = new HttpClient();

        // GET overlays/roads/:zoom/:x/:y.png
        [HttpGet("{zoom}/{x}/{y}.png")]
        public async Task<ActionResult> Get(int zoom, int x, int y)
        {
            if (zoom < 18) {
                return BadRequest();
            }

            GetLatLonBoxFromTile(zoom, x, y, out var nw, out var se);
            var bbox = GetBoundingBoxFromLatLonBox(nw, se, 0.5);
            var overpassQuery = $"[out:json][timeout:60];(way[\"highway\"]({bbox}););out body;>;out skel qt;";

            OverpassResponse overpass;
            using (var overpassResponse = await Client.PostAsync(
                OverpassAPIEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>() {
                    { "data", overpassQuery },
                })
            ))
            using (var overpassReader = new StreamReader(await overpassResponse.Content.ReadAsStreamAsync()))
            using (var overpassJson = new JsonTextReader(overpassReader)) {
                overpass = new JsonSerializer().Deserialize<OverpassResponse>(overpassJson);
                // while (await overpassJson.ReadAsync()) {
                //     Console.WriteLine($"JSON  {overpassJson.Path}  {overpassJson.TokenType}  {overpassJson.ValueType}  {overpassJson.Value}");
                // }
            }

            var ways = overpass.elements.Where(element => element.type == "way").ToArray();
            var nodes = overpass.elements.Where(element => element.type == "node").ToArray();
            var nodesById = new Dictionary<long, OverpassResponseElement>(
                nodes.Select(node => {
                    return new KeyValuePair<long, OverpassResponseElement>(
                        node.id,
                        node
                    );
                })
            );

            var C = 40075016.686;
            var imageScale = (C * Math.Cos(nw.Lat) / Math.Pow(2, zoom + 8));
            var laneWidth = (float)(2 / imageScale);

            var sidewalkColor = new Rgba32(128, 128, 128);
            var kerbColor = new Rgba32(64, 64, 64);
            var roadColor = new Rgba32(192, 192, 192);
            var laneLine = new Pen<Rgba32>(new Rgba32(255, 255, 255), 1, new float[] {
                10,
                5,
            });

            var image = new Image<Rgba32>(256, 256);
            image.Mutate(context =>
            {
                RenderWays(ways, (way) => {
                    var lanes = GetLanes(way);
                    var sidewalk = way.tags.GetValueOrDefault("sidewalk", "no");
                    var sidewalkLeft = sidewalk == "both" || sidewalk == "left";
                    var sidewalkRight = sidewalk == "both" || sidewalk == "right";
                    if (lanes.Count > 0 && (sidewalkLeft || sidewalkRight)) {
                        RenderWaySegments(way, nodesById, (node1, node2) => {
                            var point1 = GetPointFromNode(nw, se, node1);
                            var point2 = GetPointFromNode(nw, se, node2);
                            var dir = Vector(point1, point2);
                            var offDir = Flip(dir);
                            var halfWidth = laneWidth * lanes.Sum() / 2 + 2;
                            var offsetLeft = halfWidth + (sidewalkLeft ? laneWidth / 2 : 0);
                            var offsetRight = halfWidth + (sidewalkRight ? laneWidth / 2 : 0);
                            context.FillPolygon(
                                sidewalkColor,
                                Offset(point1, offDir, -offsetLeft),
                                Offset(point1, dir, -halfWidth),
                                Offset(point1, offDir, offsetRight),
                                Offset(point2, offDir, offsetRight),
                                Offset(point2, dir, halfWidth),
                                Offset(point2, offDir, -offsetLeft)
                            );
                        });
                    }
                });
                RenderWays(ways, (way) => {
                    var lanes = GetLanes(way);
                    if (lanes.Count > 0) {
                        RenderWaySegments(way, nodesById, (node1, node2) => {
                            var point1 = GetPointFromNode(nw, se, node1);
                            var point2 = GetPointFromNode(nw, se, node2);
                            var dir = Vector(point1, point2);
                            var offDir = Flip(dir);
                            var halfWidth = laneWidth * lanes.Sum() / 2 + 2;
                            context.FillPolygon(
                                kerbColor,
                                Offset(point1, offDir, halfWidth),
                                Offset(point1, dir, -halfWidth),
                                Offset(point1, offDir, -halfWidth),
                                Offset(point2, offDir, -halfWidth),
                                Offset(point2, dir, halfWidth),
                                Offset(point2, offDir, halfWidth)
                            );
                        });
                    }
                });
                RenderWays(ways, (way) => {
                    var lanes = GetLanes(way);
                    if (lanes.Count > 0) {
                        RenderWaySegments(way, nodesById, (node1, node2) => {
                            var point1 = GetPointFromNode(nw, se, node1);
                            var point2 = GetPointFromNode(nw, se, node2);
                            var dir = Vector(point1, point2);
                            var offDir = Flip(dir);
                            var halfWidth = laneWidth * lanes.Sum() / 2 + 1;
                            context.FillPolygon(
                                roadColor,
                                Offset(point1, offDir, halfWidth),
                                Offset(point1, dir, -halfWidth),
                                Offset(point1, offDir, -halfWidth),
                                Offset(point2, offDir, -halfWidth),
                                Offset(point2, dir, halfWidth),
                                Offset(point2, offDir, halfWidth)
                            );
                        });
                    }
                });
                RenderWays(ways, (way) => {
                    var lanes = GetLanes(way);
                    if (lanes.Count > 1) {
                        RenderWaySegments(way, nodesById, (node1, node2) => {
                            var point1 = GetPointFromNode(nw, se, node1);
                            var point2 = GetPointFromNode(nw, se, node2);
                            var dir = Vector(point1, point2);
                            var offDir = Flip(dir);
                            var laneOffset = -lanes.Sum() / 2;
                            for (var laneIndex = 0; laneIndex < lanes.Count - 1; laneIndex++) {
                                laneOffset += lanes[laneIndex];
                                context.DrawLines(
                                    laneLine,
                                    Offset(point1, offDir, laneWidth * laneOffset),
                                    Offset(point2, offDir, laneWidth * laneOffset)
                                );
                            }
                        });
                    }
                });
            });

            Console.WriteLine($"{DateTimeOffset.Now}  Tile generated: {zoom}/{x}/{y}.png");

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;

            return File(stream, "image/png");
        }

        static List<float> GetLanes(OverpassResponseElement way) {
            var lanes = new List<float>();
            var drivingLanes = GetDrivingLanes(way);
            if (drivingLanes == 0) {
                return lanes;
            }
            for (var i = 0; i < drivingLanes; i++) {
                lanes.Add(1);
            }
            var parkingLeftLanes = GetParkingLanes(way, ":left");
            if (parkingLeftLanes > 0) {
                lanes.Insert(0, parkingLeftLanes);
            }
            var parkingRightLanes = GetParkingLanes(way, ":right");
            if (parkingRightLanes > 0) {
                lanes.Add(parkingRightLanes);
            }
            var parkingBothLanes = GetParkingLanes(way, ":both");
            if (parkingBothLanes > 0) {
                lanes.Insert(0, parkingBothLanes);
                lanes.Add(parkingBothLanes);
            }
            if (way.tags.GetValueOrDefault("cycleway", "no") == "lane") {
                lanes.Insert(0, LaneWidthCycle);
                lanes.Add(LaneWidthCycle);
            } else if (way.tags.GetValueOrDefault("cycleway", "no") == "opposite") {
                lanes.Add(LaneWidthCycle);
            } else {
                if (way.tags.GetValueOrDefault("cycleway:left", "no") == "lane") {
                    lanes.Insert(0, LaneWidthCycle);
                }
                if (way.tags.GetValueOrDefault("cycleway:right", "no") == "lane") {
                    lanes.Add(LaneWidthCycle);
                }
            }
            return lanes;
        }

        static int GetDrivingLanes(OverpassResponseElement way) {
            switch (way.tags.GetValueOrDefault("highway", "no")) {
                case "motorway":
                case "trunk":
                case "primary":
                case "secondary":
                case "tertiary":
                case "unclassified":
                case "residential":
                case "service":
                case "motorway_link":
                case "trunk_link":
                case "primary_link":
                case "secondary_link":
                case "tertiary_link":
                    var defaultLanes = way.tags.GetValueOrDefault("oneway", "no") == "yes" ? "1" : "2";
                    return int.Parse(way.tags.GetValueOrDefault("lanes", defaultLanes));
                default:
                    return 0;
            }
        }

        static float GetParkingLanes(OverpassResponseElement way, string side) {
            switch (way.tags.GetValueOrDefault("parking:lane" + side, "no")) {
                case "parallel":
                    return 1;
                case "diagonal":
                    return 1.5f;
                case "perpendicular":
                    return 2;
                default:
                    return 0;
            }
        }

        static void RenderWays(OverpassResponseElement[] ways, Action<OverpassResponseElement> render)
        {
            foreach (var way in ways)
            {
                render(way);
            }
        }

        static void RenderWaySegments(OverpassResponseElement way, Dictionary<long, OverpassResponseElement> nodesById, Action<OverpassResponseElement, OverpassResponseElement> render)
        {
            for (var i = 1; i < way.nodes.Length; i++)
            {
                render(nodesById[way.nodes[i - 1]], nodesById[way.nodes[i]]);
            }
        }
    }
}