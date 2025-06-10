namespace sciana2
{
    public class LayoutService
    {
        public BuildingLayout CreateLayout(InputParameters input)
        {
            LayoutGenerator generator = new LayoutGenerator();
            BuildingLayout layout = generator.GenerateLayout(input.HasGarage);

            // Modyfikacja wymiarów na podstawie danych wejściowych
            foreach (var room in layout.Rooms)
            {
                if (input.CustomWidth > 0)
                {
                    room.Width = input.CustomWidth;
                }
                if (input.CustomDepth > 0)
                {
                    room.Depth = input.CustomDepth;
                }
            }

            return layout;
        }
    }
}
