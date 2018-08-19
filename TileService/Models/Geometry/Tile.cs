using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SixLabors.Primitives;

namespace TileService.Models.Geometry
{
    public class Tile
    {
        const double C = 40075016.686;
        const int MaximumCachedZoomLevel = 14;
        const int MaximumCachedTiles = 16;
        static readonly Dictionary<string, Task<Tile>> TileCache = new Dictionary<string, Task<Tile>>();
        static readonly List<string> TileCacheOrder = new List<string>();

        public static async Task<Tile> Get(int zoom, int x, int y)
        {
            Debug.Assert(zoom >= MaximumCachedZoomLevel, "Cannot load tile with zoom {zoom} < {MaximumCachedZoomLevel}");

            var zoomDiff = zoom - MaximumCachedZoomLevel;
            var cachedTile = await GetCached(MaximumCachedZoomLevel, (int)(x / Math.Pow(2, zoomDiff)), (int)(y / Math.Pow(2, zoomDiff)));

            return new Tile(zoom, x, y, cachedTile);
        }

        static Task<Tile> GetCached(int zoom, int x, int y)
        {
            var key = $"{zoom}/{x}/{y}";
            lock (TileCache) {
                if (TileCache.TryGetValue(key, out var task)) {
                    TileCacheOrder.Remove(key);
                    TileCacheOrder.Add(key);
                    return task;
                }

                var tile = new Tile(zoom, x, y);
                task = tile.LoadGeometry();
                TileCache.Add(key, task);
                TileCacheOrder.Add(key);

                while (TileCacheOrder.Count > MaximumCachedTiles) {
                    TileCache.Remove(TileCacheOrder[0]);
                    TileCacheOrder.RemoveAt(0);
                }

                Console.WriteLine($"Caching {tile} ({TileCacheOrder.Count} / {MaximumCachedTiles})");

                return task;
            }
        }

        public int Zoom { get; }
        public int X { get; }
        public int Y { get; }
        public Point NW { get; }
        public Point SE { get; }
        public float ImageScale { get; }
        public ImmutableList<string> Layers { get; private set; }
        public ImmutableList<Way> Roads { get; private set; }
        public ImmutableList<Junction> RoadJunctions { get; private set; }

        Tile(int zoom, int x, int y)
        {
            Zoom = zoom;
            X = x;
            Y = y;
            NW = GetPointFromTile(zoom, x, y);
            SE = GetPointFromTile(zoom, x + 1, y + 1);
            ImageScale = (float)(1 / (C * Math.Cos(NW.Lat * Math.PI / 180) / Math.Pow(2, zoom + 8)));
        }

        Tile(int zoom, int x, int y, Tile copy)
            : this(zoom, x, y)
        {
            Debug.Assert(copy.Layers != null, "Cannot copy data from Tile without any data");

            Layers = copy.Layers;
            Roads = copy.Roads;
            RoadJunctions = copy.RoadJunctions;
        }

        async Task<Tile> LoadGeometry()
        {
            Debug.Assert(Zoom == MaximumCachedZoomLevel, $"Trying to load geometry for incorrect zoom level {Zoom}");
            Debug.Assert(Roads == null, "Cannot load data for Tile more than once");

            var overpass = await Overpass.Query.Get(this);
            var overpassWays = overpass.elements.Where(element => element.type == "way").ToArray();
            var overpassNodes = overpass.elements.Where(element => element.type == "node").ToArray();
            var overpassNodesById = new Dictionary<long, Overpass.Element>(
                overpassNodes.Select(node => {
                    return new KeyValuePair<long, Overpass.Element>(
                        node.id,
                        node
                    );
                })
            );

            var overpassRoads = overpassWays.Where(way => {
                if (way.tags.GetValueOrDefault("area", "no") == "yes") {
                    return false;
                }
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
                        return true;
                }
                return false;
            }).ToList();
            var overpassRoadJunctions = overpassNodes.Where(node => {
                return 1 < overpassRoads.Where(road => road.nodes.Contains(node.id)).Count();
            });

            Layers = ImmutableList.ToImmutableList(overpassWays.Select(way => {
                return way.tags.GetValueOrDefault("layer", "0");
            }).Distinct().Where(layer => int.TryParse(layer, out var result))).Sort((a, b) => {
                return int.Parse(a) - int.Parse(b);
            });

            Roads = ImmutableList.ToImmutableList(overpassRoads.Select(way => {
                return new Way(
                    this,
                    way.tags,
                    way.nodes.Select(nodeId => {
                        var node = overpassNodesById[nodeId];
                        return new Point(node.lat, node.lon);
                    })
                );
            }));
            RoadJunctions = ImmutableList.ToImmutableList(overpassRoadJunctions.Select(node => {
                return new Junction(
                    overpassRoads.Where(road => road.nodes.Contains(node.id)).Select(road => {
                        var way = Roads[overpassRoads.IndexOf(road)];
                        var point = way.Points[Array.IndexOf(road.nodes, node.id)];
                        return new WayPoint(way, point);
                    })
                );
            }));

            return this;
        }

        public override string ToString()
        {
            return $"Tile({Zoom}, {X}, {Y})";
        }

        public PointF GetPointFromPoint(Point point)
        {
            return new PointF(
                (float)(256 * (point.Lon - NW.Lon) / (SE.Lon - NW.Lon)),
                (float)(256 * (point.Lat - NW.Lat) / (SE.Lat - NW.Lat))
            );
        }

        static Point GetPointFromTile(int zoom, int x, int y)
        {
            var n = Math.Pow(2, zoom);
            return new Point(
                180 / Math.PI * Math.Atan(Math.Sinh(Math.PI - (2 * Math.PI * y / n))),
                (x / n * 360) - 180
            );
        }
    }
}