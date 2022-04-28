namespace CrazyBike.Shared
{
    public class Bike
    {
        public string Id { get; set; }
        public int Price { get; set; }
        public string Model { get; set; }
        public List<BikePart> Parts { get; set; }
    }
}