using System;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal static class AwardLocator
    {
        static readonly Regex Pattern = new Regex(@"\A[A-R]{2}[0-9]{2}(?:[A-X]{2})?\z", RegexOptions.CultureInvariant);

        internal static string Parse(string value)
        {
            string grid = Full(value);
            return grid == null ? null : grid.Substring(0, 4);
        }

        internal static string Full(string value)
        {
            string grid = (value ?? "").Trim().ToUpperInvariant();
            return grid != "RR73" && Pattern.IsMatch(grid) ? grid : null;
        }

        // A decoded grid describes an area. Subtract each area's maximum travel from its centre
        // so a contact near the threshold is only selected when the grids establish the distance.
        internal static bool FartherThan(string station, string remote, int kilometres)
        {
            station = Full(station);
            remote = Full(remote);
            if (station == null || remote == null) return false;
            double latitudeA, longitudeA, radiusA, latitudeB, longitudeB, radiusB;
            Centre(station, out latitudeA, out longitudeA, out radiusA);
            Centre(remote, out latitudeB, out longitudeB, out radiusB);
            double dy = Math.Sin((latitudeB - latitudeA) / 2), dx = Math.Sin((longitudeB - longitudeA) / 2);
            double a = dy * dy + Math.Cos(latitudeA) * Math.Cos(latitudeB) * dx * dx;
            const double earthRadiusKm = 6371.0088;
            return earthRadiusKm * (2 * Math.Asin(Math.Sqrt(Math.Min(1, a))) - radiusA - radiusB) > kilometres;
        }

        static void Centre(string grid, out double latitude, out double longitude, out double radius)
        {
            double west = -180 + (grid[0] - 'A') * 20 + (grid[2] - '0') * 2;
            double south = -90 + (grid[1] - 'A') * 10 + grid[3] - '0';
            double width = 2, height = 1;
            if (grid.Length == 6)
            {
                width /= 24; height /= 24;
                west += (grid[4] - 'A') * width;
                south += (grid[5] - 'A') * height;
            }
            const double radians = Math.PI / 180;
            latitude = (south + height / 2) * radians;
            longitude = (west + width / 2) * radians;
            radius = (height + width) * radians / 2;
        }
    }

    internal static class HamlogRdaCode
    {
        static readonly Regex Pattern = new Regex(@"\A[A-Z]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
        internal static bool IsCounty(string value) { return Pattern.IsMatch(value ?? ""); }
    }
}
