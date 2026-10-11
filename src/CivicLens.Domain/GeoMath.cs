using NetTopologySuite.Geometries;

namespace CivicLens.Domain;

public static class GeoMath
{
    public const double EarthRadiusMeters = 6_371_008.8;
    public const int Wgs84 = 4326;

    private static readonly GeometryFactory Factory = new(new PrecisionModel(), Wgs84);

    /// <summary>NTS point: X = longitude, Y = latitude (PostGIS convention).</summary>
    public static Point ToPoint(double latitude, double longitude) => Factory.CreatePoint(new Coordinate(longitude, latitude));

    public static Polygon ToPolygon(IEnumerable<(double Lat, double Lon)> ring)
    {
        var coords = ring.Select(p => new Coordinate(p.Lon, p.Lat)).ToList();
        if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
        return Factory.CreatePolygon(coords.ToArray());
    }

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Cheap bounding box used to pre-filter candidates before exact distance checks.</summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon) BoundingBox(double lat, double lon, double radiusMeters)
    {
        var dLat = radiusMeters / EarthRadiusMeters * 180 / Math.PI;
        var dLon = dLat / Math.Max(0.01, Math.Cos(ToRad(lat)));
        return (lat - dLat, lat + dLat, lon - dLon, lon + dLon);
    }

    public static bool IsValidCoordinate(double lat, double lon) =>
        !double.IsNaN(lat) && !double.IsNaN(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private static double ToRad(double deg) => deg * Math.PI / 180;
}
