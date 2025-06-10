using System.Collections.Generic;

namespace sciana2
{
    public class BuildingLayout
    {
        public List<RoomParameters> Rooms { get; set; } = new List<RoomParameters>();
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }
}
