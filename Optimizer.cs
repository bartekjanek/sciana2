using System.Linq;

namespace sciana2
{
    public class Optimizer
    {
        public BuildingLayout OptimizeLayout(BuildingLayout layout)
        {
            // Przykładowa optymalizacja: minimalizacja powierzchni
            double totalArea = layout.Rooms.Sum(r => r.Width * r.Depth);
            // Logika optymalizacji
            // ...

            return layout;
        }
    }
}
