namespace sciana2
{
    public class LayoutGenerator
    {
        public BuildingLayout GenerateLayout(bool hasGarage)
        {
            BuildingLayout layout = new BuildingLayout();

            // Dodajemy parametry pomieszcze?
            layout.Rooms.Add(new RoomParameters { Name = "Wiatro?ap", Width = 2.5, Depth = 2.0, IsRequired = true });
            layout.Rooms.Add(new RoomParameters { Name = "WC", Width = 1.5, Depth = 2.0, IsRequired = true });
            if (hasGarage)
            {
                layout.Rooms.Add(new RoomParameters { Name = "Gara?", Width = 4.0, Depth = 6.0, IsRequired = true });
                layout.Rooms.Add(new RoomParameters { Name = "Kot?ownia", Width = 2.0, Depth = 2.5, IsRequired = true });
            }
            layout.Rooms.Add(new RoomParameters { Name = "Hall / Korytarz", Width = 2.0, Depth = 8.0, IsRequired = true });
            layout.Rooms.Add(new RoomParameters { Name = "Salon + Jadalnia + Kuchnia", Width = 8.0, Depth = 8.0, IsRequired = true });
            layout.Rooms.Add(new RoomParameters { Name = "Sypialnia g?ówna", Width = 4.0, Depth = 4.0, IsRequired = true });
            layout.Rooms.Add(new RoomParameters { Name = "Sypialnia dodatkowa", Width = 3.5, Depth = 4.0, IsRequired = true });
            layout.Rooms.Add(new RoomParameters { Name = "?azienka", Width = 2.5, Depth = 2.5, IsRequired = true });

            // Logika generowania uk?adu
            // ...

            return layout;
        }
    }
}
