#region Namespaces
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NTSPoint = NetTopologySuite.Geometries.Point;
using NetTopologySuite.Triangulate;
#endregion

namespace sciana2
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        // Stałe definiujące minimalną i maksymalną powierzchnię sub-parceli
        private const double MIN_PARCEL_AREA = 6458.0;   // [m²]
        private const double MAX_PARCEL_AREA = 10764.0;  // [m²]

        // Szerokość drogi
        private const double ROAD_WIDTH = 6.0; // [m] - przykładowa szerokość drogi

        // Wysokość budynku (można ją uczynić parametryczną)
        private const double BUILDING_HEIGHT = 10.0; // [m]

        // Minimalna odległość między drogą a budynkiem [m]
        private const double MIN_ROAD_BUILDING_DISTANCE = 2.0;

        // Maksymalna liczba prób przesunięcia budynku
        private const int MAX_SHIFT_ATTEMPTS = 10;

        // Krok przesunięcia [m]
        private const double SHIFT_STEP = 0.5;

        // Generator liczb losowych wykorzystywany w klasie
        private static readonly Random _rnd = new Random();

        // Lista przechowująca linie dróg
        private List<Line> roadLines = new List<Line>();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Revit Plugin",
                    "Otwarty dokument jest dokumentem rodziny (RFA). Potrzebny dokument projektu (RVT).");
                return Result.Failed;
            }

            try
            {
                TaskDialog.Show("Revit Plugin",
                    "Uruchamiam narzędzie - podział nieregularnej działki (multi-loops + korekcja siatki).");

                // 1. Wybór obszaru wypełnienia (FilledRegion) reprezentującego działkę
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Wskaż obszar wypełnienia (FilledRegion) reprezentujący działkę");

                Element element = doc.GetElement(pickedRef);
                if (!(element is FilledRegion))
                {
                    TaskDialog.Show("Revit Plugin",
                        "Wybrany element nie jest obszarem wypełnienia (FilledRegion).");
                    return Result.Failed;
                }

                FilledRegion filledRegion = element as FilledRegion;
                IList<CurveLoop> boundaryLoops = filledRegion.GetBoundaries();
                if (boundaryLoops == null || boundaryLoops.Count == 0)
                {
                    TaskDialog.Show("Revit Plugin",
                        "Nie można pobrać granic obszaru wypełnienia.");
                    return Result.Failed;
                }

                // Przyjmujemy pierwszą pętlę jako główną
                CurveLoop mainBoundary = boundaryLoops[0];

                // 2. Informacje o działce
                string shapeType = DetectBoundaryShape(mainBoundary);
                double totalArea = CalculateArea2D(mainBoundary);

                TaskDialog.Show("Revit Plugin",
                    $"Kształt działki: {shapeType}\n" +
                    $"Powierzchnia (2D): {totalArea:F2} m²");

                // 3. Wskazujemy jedynie punkt wjazdu (driveway point) na granicy działki
                XYZ drivewayPoint = uidoc.Selection.PickPoint("Wskaż punkt wjazdu na działkę");

                // 4. Podział działki na sub-parcele
                List<(CurveLoop, double)> parcels = null;
                using (Transaction tx = new Transaction(doc, "Podział działki (Voronoi)"))
                {
                    tx.Start();

                    parcels = CreateSubParcelsVoronoi(mainBoundary);

                    TaskDialog.Show("Revit Plugin",
                        $"Utworzono sub-parcel: {parcels.Count}");

                    tx.Commit();
                }

                // 5. Tworzenie dróg (główna droga + odgałęzienia) i budynków
                using (Transaction tx = new Transaction(doc, "Tworzenie dróg (główna + odgałęzienia) i budynków"))
                {
                    tx.Start();

                    // Tworzymy główną drogę wejściową i odgałęzienia do sub-parceli
                    CreateRoadSpineWithBranches(doc, drivewayPoint, parcels);

                    // Rysowanie obrysów sub-parcel i wstawianie budynków
                    foreach (var (loop, area) in parcels)
                    {
                        DrawParcel(doc, loop);
                        PlaceBuilding(doc, loop);
                    }

                    tx.Commit();
                }

                TaskDialog.Show("Revit Plugin",
                    "Zakończono podział (korekcja siatki) i tworzenie elementów.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message + "\n" + ex.StackTrace;
                TaskDialog.Show("Revit Plugin", "Błąd: " + message);
                return Result.Failed;
            }
        }

        #region Spłaszczanie Z

        private XYZ FlattenXYZ(XYZ p)
        {
            return new XYZ(p.X, p.Y, 0.0);
        }

        private Line CreateFlatLine(XYZ start, XYZ end)
        {
            XYZ s = FlattenXYZ(start);
            XYZ e = FlattenXYZ(end);
            return Line.CreateBound(s, e);
        }

        #endregion

        #region Podział działki (z korekcją kolumn/wierszy), wielopętlowy

        private List<(CurveLoop, double)> CreateSubParcelsWithClippingMulti(CurveLoop boundaryLoop)
        {
            List<(CurveLoop, double)> result = new List<(CurveLoop, double)>();

            // 1. Bounding box
            BoundingBoxXYZ bbox = CalculateBoundingBox(boundaryLoop);
            double width = bbox.Max.X - bbox.Min.X;
            double height = bbox.Max.Y - bbox.Min.Y;

            // 2. Ustal docelową (średnią) powierzchnię i wstępnie wylicz liczbę kolumn/wierszy
            double targetParcelArea = (MIN_PARCEL_AREA + MAX_PARCEL_AREA) / 2.0;

            int columns = Math.Max(1, (int)Math.Floor(width / Math.Sqrt(targetParcelArea)));
            int rows = Math.Max(1, (int)Math.Floor(height / Math.Sqrt(targetParcelArea)));

            double cellWidth = (columns > 0) ? width / columns : width;
            double cellHeight = (rows > 0) ? height / rows : height;
            double cellArea = cellWidth * cellHeight;

            // 3. Prosta heurystyka korygująca
            while (cellArea < MIN_PARCEL_AREA && (columns > 1 || rows > 1))
            {
                if (columns > 1) columns = Math.Max(1, columns - 1);
                else if (rows > 1) rows = Math.Max(1, rows - 1);

                cellWidth = width / columns;
                cellHeight = height / rows;
                cellArea = cellWidth * cellHeight;
            }

            while (cellArea > MAX_PARCEL_AREA && (columns < 999 || rows < 999))
            {
                if (columns < 999) columns++;
                else if (rows < 999) rows++;

                cellWidth = width / columns;
                cellHeight = height / rows;
                cellArea = cellWidth * cellHeight;
            }

            // 4. Tworzymy siatkę i przycinamy każdą komórkę do boundaryLoop
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < columns; j++)
                {
                    double x1 = bbox.Min.X + j * cellWidth;
                    double y1 = bbox.Min.Y + i * cellHeight;
                    double x2 = x1 + cellWidth;
                    double y2 = y1 + cellHeight;

                    CurveLoop cellLoop = CurveLoop.Create(new List<Curve>
                    {
                        CreateFlatLine(new XYZ(x1, y1, 0), new XYZ(x2, y1, 0)),
                        CreateFlatLine(new XYZ(x2, y1, 0), new XYZ(x2, y2, 0)),
                        CreateFlatLine(new XYZ(x2, y2, 0), new XYZ(x1, y2, 0)),
                        CreateFlatLine(new XYZ(x1, y2, 0), new XYZ(x1, y1, 0))
                    });

                    List<CurveLoop> clippedLoops = IntersectParcelWithBoundaryMulti(boundaryLoop, cellLoop);

                    foreach (CurveLoop subLoop in clippedLoops)
                    {
                        double area = CalculateArea2D(subLoop);
                        if (area > 1e-6)
                        {
                            result.Add((subLoop, area));
                        }
                    }
                }
            }

            return result;
        }

        private List<CurveLoop> IntersectParcelWithBoundaryMulti(CurveLoop boundaryLoop, CurveLoop cellLoop)
        {
            Solid boundarySolid = CreateExtrusionFromCurveLoop(boundaryLoop, 0.0, 1.0);
            Solid cellSolid = CreateExtrusionFromCurveLoop(cellLoop, 0.0, 1.0);

            Solid intersectSolid = BooleanOperationsUtils.ExecuteBooleanOperation(
                boundarySolid, cellSolid, BooleanOperationsType.Intersect);

            List<CurveLoop> loops = new List<CurveLoop>();
            if (intersectSolid == null || intersectSolid.Volume < 1e-9)
            {
                return loops;
            }

            Face topFace = GetTopFace(intersectSolid);
            if (topFace == null) return loops;

            IList<CurveLoop> faceLoops = topFace.GetEdgesAsCurveLoops();
            if (faceLoops == null || faceLoops.Count == 0) return loops;

            foreach (CurveLoop loop in faceLoops)
            {
                double area = CalculateArea2D(loop);
                if (area > 1e-6)
                {
                    loops.Add(loop);
                }
            }

            return loops;
        }

        private Solid CreateExtrusionFromCurveLoop(CurveLoop loop, double startZ, double endZ)
        {
            CurveLoop baseLoop = new CurveLoop();
            foreach (Curve c in loop)
            {
                XYZ p1 = c.GetEndPoint(0);
                XYZ p2 = c.GetEndPoint(1);

                XYZ p1z = new XYZ(p1.X, p1.Y, startZ);
                XYZ p2z = new XYZ(p2.X, p2.Y, startZ);

                baseLoop.Append(Line.CreateBound(p1z, p2z));
            }

            double height = endZ - startZ;
            if (height <= 0)
            {
                height = 0.1; // Minimalna dodatnia wartość
            }

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { baseLoop },
                XYZ.BasisZ,
                height
            );
        }

        private Face GetTopFace(Solid solid)
        {
            foreach (Face f in solid.Faces)
            {
                BoundingBoxUV uv = f.GetBoundingBox();
                double midU = (uv.Min.U + uv.Max.U) / 2.0;
                double midV = (uv.Min.V + uv.Max.V) / 2.0;

                XYZ normal = f.ComputeNormal(new UV(midU, midV));
                if (normal.Z > 0.9999)
                {
                    return f;
                }
            }
            return null;
        }

        #endregion

        #region Podział działki (diagram Voronoi)

        private List<(CurveLoop, double)> CreateSubParcelsVoronoi(CurveLoop boundaryLoop)
        {
            var result = new List<(CurveLoop, double)>();

            // 1. Konwersja granicy na poligon NTS
            Polygon originalPolygon = CurveLoopToPolygon(boundaryLoop);
            if (originalPolygon == null || originalPolygon.IsEmpty)
                return result;

            double totalArea = originalPolygon.Area;
            double targetArea = (MIN_PARCEL_AREA + MAX_PARCEL_AREA) / 2.0;
            int parcelCount = Math.Max(1, (int)Math.Round(totalArea / targetArea));

            Envelope env = originalPolygon.EnvelopeInternal;

            // 2. Generowanie punktów startowych wewnątrz poligonu
            List<Coordinate> seeds = new List<Coordinate>();
            while (seeds.Count < parcelCount)
            {
                double x = env.MinX + _rnd.NextDouble() * (env.MaxX - env.MinX);
                double y = env.MinY + _rnd.NextDouble() * (env.MaxY - env.MinY);
                Coordinate c = new Coordinate(x, y);
                if (originalPolygon.Contains(new NTSPoint(c)))
                {
                    seeds.Add(c);
                }
            }

            // 3. Budowanie diagramu Voronoi
            VoronoiDiagramBuilder vdb = new VoronoiDiagramBuilder();
            vdb.SetSites(seeds);
            vdb.ClipEnvelope = env;
            GeometryCollection diagram = (GeometryCollection)vdb.GetDiagram(new GeometryFactory());

            // 4. Intersekcja komórek z oryginalnym poligonem
            foreach (Geometry g in diagram.Geometries)
            {
                if (g is Polygon cell)
                {
                    Geometry clipped = cell.Intersection(originalPolygon);
                    foreach (var loop in NtsGeometryToCurveLoops(clipped))
                    {
                        double area = CalculateArea2D(loop);
                        if (area > 1e-6)
                        {
                            result.Add((loop, area));
                        }
                    }
                }
            }

            return result;
        }

        #endregion

        #region Tworzenie dróg (główna droga wejściowa + odgałęzienia)

        /// <summary>
        /// Tworzy główną drogę wejściową wzdłuż granic sub-parcels oraz odgałęzienia do sub-parcels jako najkrótsze ścieżki.
        /// Główna droga przebiega wzdłuż wspólnych granic między sub-parcels.
        /// </summary>
        private void CreateRoadSpineWithBranches(Document doc, XYZ drivewayPoint, List<(CurveLoop, double)> parcels)
        {
            // 1. Identyfikacja wspólnych granic między sub-parcels
            List<Line> sharedBoundaries = GetSharedBoundaries(parcels.Select(p => p.Item1).ToList());

            // 2. Tworzenie głównej drogi wzdłuż wspólnych granic
            foreach (Line sharedLine in sharedBoundaries)
            {
                roadLines.Add(sharedLine); // Dodajemy linię drogi do listy

                Solid roadSolid = CreateRoadSolid(sharedLine, ROAD_WIDTH);

                // Klipowanie drogi do granic działki
                Solid clippedRoad = ClipSolidToParcels(doc, roadSolid, parcels);

                if (clippedRoad != null && clippedRoad.Volume > 1e-6)
                {
                    // Tworzenie DirectShape dla drogi
                    DirectShape dsRoad = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    dsRoad.SetShape(new List<GeometryObject> { clippedRoad });
                }
            }

            // 3. Tworzenie dróg do budynków jako najkrótsze ścieżki
            foreach (var (parcelLoop, parcelArea) in parcels)
            {
                // Obliczenie centroidu sub-parcely
                XYZ parcelCentroid = GetParcelCentroid(parcelLoop);
                XYZ parcelCentroidFlat = FlattenXYZ(parcelCentroid);

                // Znalezienie najbliższej głównej drogi
                Line nearestRoad = FindNearestRoad(parcelCentroidFlat, sharedBoundaries);

                if (nearestRoad != null)
                {
                    // Rzutowanie centroidu sub-parcely na najbliższą główną drogę
                    XYZ projectedPoint = ProjectPointOnLine(parcelCentroidFlat, nearestRoad);

                    // Tworzenie linii od najbliższej głównej drogi do centroidu sub-parcely
                    Line branchLine = CreateFlatLine(projectedPoint, parcelCentroidFlat);
                    roadLines.Add(branchLine); // Dodajemy linię odgałęzienia do listy

                    Solid branchSolid = CreateRoadSolid(branchLine, ROAD_WIDTH);

                    // Klipowanie drogi do granic działki
                    Solid clippedBranch = ClipSolidToParcels(doc, branchSolid, parcels);

                    if (clippedBranch != null && clippedBranch.Volume > 1e-6)
                    {
                        // Tworzenie DirectShape dla drogi do budynku
                        DirectShape dsBranch = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        dsBranch.SetShape(new List<GeometryObject> { clippedBranch });
                    }
                }
            }
        }

        /// <summary>
        /// Zwraca listę wspólnych granic (jako linie) między sub-parcels.
        /// </summary>
        private List<Line> GetSharedBoundaries(List<CurveLoop> parcelLoops)
        {
            List<Line> sharedLines = new List<Line>();

            // Przechodzimy przez każdą parę sub-parcels
            for (int i = 0; i < parcelLoops.Count; i++)
            {
                for (int j = i + 1; j < parcelLoops.Count; j++)
                {
                    CurveLoop loop1 = parcelLoops[i];
                    CurveLoop loop2 = parcelLoops[j];

                    // Konwertujemy CurveLoop na listę Curve dla łatwiejszego dostępu
                    List<Curve> curves1 = loop1.ToList();
                    List<Curve> curves2 = loop2.ToList();

                    // Znajdujemy wspólne linie
                    var commonLines = curves1.Where(c1 => curves2.Any(c2 => AreLinesEqual(c1 as Line, c2 as Line)));

                    foreach (var line in commonLines)
                    {
                        if (line is Line sharedLine)
                        {
                            // Dodajemy unikalne linie do listy
                            if (!sharedLines.Any(l => AreLinesEqual(l, sharedLine)))
                            {
                                sharedLines.Add(sharedLine);
                            }
                        }
                    }
                }
            }

            return sharedLines;
        }

        /// <summary>
        /// Sprawdza, czy dwie linie są równe (uwzględniając tolerancję).
        /// </summary>
        private bool AreLinesEqual(Line line1, Line line2, double tolerance = 1e-3)
        {
            if (line1 == null || line2 == null)
                return false;

            return (line1.GetEndPoint(0).DistanceTo(line2.GetEndPoint(0)) < tolerance &&
                    line1.GetEndPoint(1).DistanceTo(line2.GetEndPoint(1)) < tolerance) ||
                   (line1.GetEndPoint(0).DistanceTo(line2.GetEndPoint(1)) < tolerance &&
                    line1.GetEndPoint(1).DistanceTo(line2.GetEndPoint(0)) < tolerance);
        }

        /// <summary>
        /// Znajduje najbliższą drogę (z listy linii) do punktu.
        /// </summary>
        private Line FindNearestRoad(XYZ point, List<Line> roads)
        {
            double minDistance = double.MaxValue;
            Line nearestRoad = null;

            foreach (Line road in roads)
            {
                double distance = CalculateDistancePointToLine(point, road);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestRoad = road;
                }
            }

            return nearestRoad;
        }

        /// <summary>
        /// Oblicza odległość punktu do linii.
        /// </summary>
        private double CalculateDistancePointToLine(XYZ point, Line line)
        {
            XYZ projection = ProjectPointOnLine(point, line);
            return point.DistanceTo(projection);
        }

        /// <summary>
        /// Rzutuje punkt 'point' na zadaną linię 'line' (2D w płaszczyźnie Z=0).
        /// </summary>
        private XYZ ProjectPointOnLine(XYZ point, Line line)
        {
            XYZ pFlat = FlattenXYZ(point);

            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);
            XYZ ab = b - a;
            XYZ ap = pFlat - a;

            double t = ap.DotProduct(ab) / ab.DotProduct(ab);
            t = Math.Max(0, Math.Min(1, t)); // Clamp t to [0,1] to stay within the segment
            return a + t * ab;
        }

        /// <summary>
        /// Tworzy solid reprezentujący drogę na podstawie linii i szerokości.
        /// </summary>
        private Solid CreateRoadSolid(Line line, double width)
        {
            // Obliczenie kierunku prostopadłego do linii
            XYZ direction = line.Direction.Normalize();
            XYZ perp = new XYZ(-direction.Y, direction.X, 0); // 90 stopni rotacji w płaszczyźnie XY

            // Obliczenie punktów prostokąta reprezentującego drogę
            double halfWidth = width / 2.0;

            XYZ p1 = line.GetEndPoint(0) + perp * halfWidth;
            XYZ p2 = line.GetEndPoint(1) + perp * halfWidth;
            XYZ p3 = line.GetEndPoint(1) - perp * halfWidth;
            XYZ p4 = line.GetEndPoint(0) - perp * halfWidth;

            // Tworzenie profilu drogi jako CurveLoop
            List<Curve> roadProfile = new List<Curve>
            {
                CreateFlatLine(p1, p2),
                CreateFlatLine(p2, p3),
                CreateFlatLine(p3, p4),
                CreateFlatLine(p4, p1)
            };

            CurveLoop roadLoop = CurveLoop.Create(roadProfile);

            // Ekstrudowanie profilu drogi w górę (w tym przypadku na wysokość 0.1 m, aby droga była płaska z minimalną grubością)
            double extrusionHeight = 0.1; // [m] - minimalna grubość drogi

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { roadLoop },
                XYZ.BasisZ,
                extrusionHeight
            );
        }

        /// <summary>
        /// Klipuje solid drogi do granic sub-parcels poprzez operację Boolean typu Intersect.
        /// </summary>
        private Solid ClipSolidToParcels(Document doc, Solid roadSolid, List<(CurveLoop, double)> parcels)
        {
            // Tworzenie jednego solidu z wszystkich sub-parcels
            Solid combinedParcelSolid = null;

            foreach (var (parcelLoop, _) in parcels)
            {
                Solid parcelSolid = CreateExtrusionFromCurveLoop(parcelLoop, 0.0, BUILDING_HEIGHT);
                if (combinedParcelSolid == null)
                {
                    combinedParcelSolid = parcelSolid;
                }
                else
                {
                    combinedParcelSolid = BooleanOperationsUtils.ExecuteBooleanOperation(
                        combinedParcelSolid, parcelSolid, BooleanOperationsType.Union);
                }
            }

            if (combinedParcelSolid == null)
                return null;

            // Klipowanie drogi do granic parceli
            Solid clippedRoad = BooleanOperationsUtils.ExecuteBooleanOperation(
                roadSolid, combinedParcelSolid, BooleanOperationsType.Intersect);

            return clippedRoad;
        }

        #endregion

        #region Rysowanie sub-parcel i wstawianie budynków (klipowane)

        private void DrawParcel(Document doc, CurveLoop parcelLoop)
        {
            foreach (Curve c in parcelLoop)
            {
                XYZ p1 = c.GetEndPoint(0);
                XYZ p2 = c.GetEndPoint(1);

                Line flatLine = CreateFlatLine(p1, p2);

                SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                doc.Create.NewModelCurve(flatLine, sp);
            }
        }

        private void PlaceBuilding(Document doc, CurveLoop parcelLoop)
        {
            // Tworzenie solidu dla sub-parcely
            Solid parcelSolid = CreateSolidFromCurveLoop(parcelLoop, BUILDING_HEIGHT);

            // Definiowanie wymiarów budynku (można to uczynić bardziej zaawansowanym)
            double buildingArea = _rnd.Next(807, 1615); // [m²] - zakres można dostosować
            double buildingWidth = Math.Sqrt(buildingArea);
            double buildingHeight = BUILDING_HEIGHT;

            // Obliczenie pozycji budynku na sub-parcely
            XYZ centroid = GetParcelCentroid(parcelLoop);
            XYZ centroidFlat = FlattenXYZ(centroid);

            // Przesuwanie budynku w celu spełnienia minimalnej odległości
            XYZ adjustedCentroidFlat = AdjustBuildingPosition(doc, parcelLoop, centroidFlat, buildingWidth);

            if (adjustedCentroidFlat == null)
            {
                TaskDialog.Show("Revit Plugin", $"Nie można umieścić budynku na działce o centroidzie ({centroidFlat.X:F2}, {centroidFlat.Y:F2}) z zachowaniem minimalnej odległości {MIN_ROAD_BUILDING_DISTANCE} m od drogi.");
                return; // Pomijamy umieszczanie budynku
            }

            // Tworzenie budynku jako solidu
            XYZ p1 = new XYZ(adjustedCentroidFlat.X - buildingWidth / 2, adjustedCentroidFlat.Y - buildingWidth / 2, 0.0);
            XYZ p2 = new XYZ(adjustedCentroidFlat.X + buildingWidth / 2, adjustedCentroidFlat.Y - buildingWidth / 2, 0.0);
            XYZ p3 = new XYZ(adjustedCentroidFlat.X + buildingWidth / 2, adjustedCentroidFlat.Y + buildingWidth / 2, 0.0);
            XYZ p4 = new XYZ(adjustedCentroidFlat.X - buildingWidth / 2, adjustedCentroidFlat.Y + buildingWidth / 2, 0.0);

            List<Curve> prof = new List<Curve>
            {
                CreateFlatLine(p1, p2),
                CreateFlatLine(p2, p3),
                CreateFlatLine(p3, p4),
                CreateFlatLine(p4, p1)
            };

            CurveLoop buildingLoop = CurveLoop.Create(prof);
            Solid buildingSolid = CreateSolidFromCurveLoop(buildingLoop, buildingHeight);

            // Klipowanie budynku do sub-parcely
            Solid clippedBuilding = BooleanOperationsUtils.ExecuteBooleanOperation(
                buildingSolid, parcelSolid, BooleanOperationsType.Intersect);

            if (clippedBuilding == null || clippedBuilding.Volume < 1e-6)
            {
                // Jeśli klipowanie nie powiodło się, pomijamy budynek
                return;
            }

            // Tworzenie DirectShape dla budynku
            DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.SetShape(new List<GeometryObject> { clippedBuilding });
        }

        /// <summary>
        /// Automatycznie przesuwa budynek, aby zapewnić minimalną odległość od dróg.
        /// </summary>
        /// <param name="doc">Dokument Revit.</param>
        /// <param name="parcelLoop">CurveLoop sub-parcely.</param>
        /// <param name="centroidFlat">Oryginalny centroid budynku (2D).</param>
        /// <param name="buildingWidth">Szerokość budynku.</param>
        /// <returns>Nowa pozycja centroidu budynku lub null, jeśli nie można spełnić wymogu.</returns>
        private XYZ AdjustBuildingPosition(Document doc, CurveLoop parcelLoop, XYZ centroidFlat, double buildingWidth)
        {
            XYZ adjustedCentroid = centroidFlat;
            int attempts = 0;
            bool positionValid = false;

            while (attempts < MAX_SHIFT_ATTEMPTS && !positionValid)
            {
                positionValid = true;

                foreach (Line road in roadLines)
                {
                    double distance = CalculateDistancePointToLine(adjustedCentroid, road);
                    if (distance < MIN_ROAD_BUILDING_DISTANCE)
                    {
                        positionValid = false;

                        // Obliczenie kierunku przesunięcia (prostopadłego do drogi)
                        XYZ roadDirection = road.Direction.Normalize();
                        XYZ perpendicular = new XYZ(-roadDirection.Y, roadDirection.X, 0);

                        // Obliczenie potrzebnego przesunięcia
                        double shiftDistance = (MIN_ROAD_BUILDING_DISTANCE - distance) + SHIFT_STEP;

                        // Przesunięcie centroidu
                        adjustedCentroid += perpendicular * shiftDistance;

                        // Sprawdzenie, czy przesunięcie nie wyjdzie poza granice sub-parcely
                        if (!IsPointInsideParcel(parcelLoop, adjustedCentroid))
                        {
                            // Jeśli punkt jest poza parcelą, spróbuj przesunąć w przeciwnym kierunku
                            adjustedCentroid -= perpendicular * (2 * shiftDistance);
                            if (!IsPointInsideParcel(parcelLoop, adjustedCentroid))
                            {
                                // Jeśli nadal poza parcelą, zakończ próby
                                return null;
                            }
                        }

                        break; // Przerwij pętlę dróg i sprawdź od nowa
                    }
                }

                attempts++;
            }

            if (positionValid)
            {
                return adjustedCentroid;
            }
            else
            {
                return null; // Nie udało się znaleźć odpowiedniej pozycji
            }
        }

        /// <summary>
        /// Sprawdza, czy punkt znajduje się wewnątrz sub-parcely.
        /// </summary>
        /// <param name="parcelLoop">CurveLoop sub-parcely.</param>
        /// <param name="point">Punkt do sprawdzenia.</param>
        /// <returns>True jeśli punkt jest wewnątrz, False w przeciwnym razie.</returns>
        private bool IsPointInsideParcel(CurveLoop parcelLoop, XYZ point)
        {
            // Używamy algorytmu ray casting do sprawdzenia, czy punkt jest wewnątrz polygonu
            int intersections = 0;
            List<Curve> curves = parcelLoop.ToList(); // Konwersja CurveLoop na listę
            int count = curves.Count;
            for (int i = 0; i < count; i++)
            {
                Curve current = curves[i];
                Curve next = curves[(i + 1) % count];

                double y = point.Y;
                double x = point.X;

                XYZ p1 = current.GetEndPoint(0);
                XYZ p2 = current.GetEndPoint(1);

                if (((p1.Y > y) != (p2.Y > y)))
                {
                    double slope = (p2.X - p1.X) / (p2.Y - p1.Y);
                    double intersectX = p1.X + slope * (y - p1.Y);
                    if (intersectX > x)
                    {
                        intersections++;
                    }
                }
            }

            return (intersections % 2) == 1;
        }

        /// <summary>
        /// Tworzy solid z CurveLoop poprzez ekstrudowanie do góry o zadaną wysokość.
        /// </summary>
        private Solid CreateSolidFromCurveLoop(CurveLoop loop, double height)
        {
            // Tworzenie pętli krzywych w dolnej płaszczyźnie
            CurveLoop baseLoop = new CurveLoop();
            foreach (Curve c in loop)
            {
                XYZ p1 = c.GetEndPoint(0);
                XYZ p2 = c.GetEndPoint(1);

                XYZ p1z = new XYZ(p1.X, p1.Y, 0.0);
                XYZ p2z = new XYZ(p2.X, p2.Y, 0.0);

                baseLoop.Append(Line.CreateBound(p1z, p2z));
            }

            // Ekstrudowanie pętli krzywych w górę
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { baseLoop },
                XYZ.BasisZ,
                height
            );
        }

        /// <summary>
        /// Oblicza centroid dla listy CurveLoop.
        /// </summary>
        private XYZ GetParcelCentroid(List<CurveLoop> loops)
        {
            // Obliczenie centroidu dla wszystkich sub-parceli
            double sx = 0, sy = 0, sz = 0;
            int count = 0;
            foreach (var loop in loops)
            {
                XYZ centroid = GetParcelCentroid(loop);
                sx += centroid.X;
                sy += centroid.Y;
                sz += centroid.Z;
                count++;
            }
            if (count == 0) return XYZ.Zero;

            double cx = sx / count;
            double cy = sy / count;
            double cz = sz / count;
            return new XYZ(cx, cy, cz);
        }

        /// <summary>
        /// Oblicza centroid dla pojedynczej CurveLoop.
        /// </summary>
        private XYZ GetParcelCentroid(CurveLoop loop)
        {
            var points = loop.Select(x => x.GetEndPoint(0)).ToList();
            if (points.Count == 0) return XYZ.Zero;

            double sx = 0, sy = 0, sz = 0;
            foreach (XYZ p in points)
            {
                sx += p.X;
                sy += p.Y;
                sz += p.Z;
            }
            double cx = sx / points.Count;
            double cy = sy / points.Count;
            double cz = sz / points.Count;
            return new XYZ(cx, cy, cz);
        }

        #endregion

        #region Metody pomocnicze (bounding box, obliczanie pola, wykrywanie kształtu)

        private BoundingBoxXYZ CalculateBoundingBox(CurveLoop loop)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (Curve c in loop)
            {
                XYZ p0 = c.GetEndPoint(0);
                XYZ p1 = c.GetEndPoint(1);

                minX = Math.Min(minX, p0.X);
                minY = Math.Min(minY, p0.Y);
                minZ = Math.Min(minZ, p0.Z);

                maxX = Math.Max(maxX, p0.X);
                maxY = Math.Max(maxY, p0.Y);
                maxZ = Math.Max(maxZ, p0.Z);

                minX = Math.Min(minX, p1.X);
                minY = Math.Min(minY, p1.Y);
                minZ = Math.Min(minZ, p1.Z);

                maxX = Math.Max(maxX, p1.X);
                maxY = Math.Max(maxY, p1.Y);
                maxZ = Math.Max(maxZ, p1.Z);
            }

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private string DetectBoundaryShape(CurveLoop boundary)
        {
            List<Curve> curves = boundary.ToList();
            if (curves.Count == 4 && curves.All(c => c is Line))
            {
                Line line1 = curves[0] as Line;
                Line line2 = curves[1] as Line;
                if (Math.Abs(line1.Direction.DotProduct(line2.Direction)) < 0.001)
                {
                    return "Prostokątna";
                }
            }
            return "Nieregularna";
        }

        private double CalculateArea2D(CurveLoop loop)
        {
            List<XYZ> pts = new List<XYZ>();
            foreach (Curve c in loop)
            {
                pts.Add(c.GetEndPoint(0));
            }
            if (pts.Count == 0) return 0;

            pts.Add(pts[0]);
            double area = 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                XYZ p1 = pts[i];
                XYZ p2 = pts[i + 1];
                area += (p1.X * p2.Y - p2.X * p1.Y);
            }
            return Math.Abs(area) / 2.0;
        }

        private Polygon CurveLoopToPolygon(CurveLoop loop)
        {
            var coords = new List<Coordinate>();
            foreach (Curve c in loop)
            {
                XYZ p = FlattenXYZ(c.GetEndPoint(0));
                coords.Add(new Coordinate(p.X, p.Y));
            }
            if (coords.Count == 0) return null;
            // ensure closed
            coords.Add(coords[0]);
            var ring = new LinearRing(coords.ToArray());
            return new Polygon(ring);
        }

        private IEnumerable<CurveLoop> NtsGeometryToCurveLoops(Geometry geom)
        {
            List<CurveLoop> loops = new List<CurveLoop>();

            if (geom == null || geom.IsEmpty)
                return loops;

            if (geom is Polygon poly)
            {
                loops.Add(PolygonToCurveLoop(poly));
            }
            else if (geom is MultiPolygon mp)
            {
                foreach (Polygon p in mp.Geometries)
                {
                    loops.Add(PolygonToCurveLoop(p));
                }
            }

            return loops;
        }

        private CurveLoop PolygonToCurveLoop(Polygon poly)
        {
            var coords = poly.ExteriorRing.Coordinates;
            List<Curve> curves = new List<Curve>();
            for (int i = 0; i < coords.Length - 1; i++)
            {
                XYZ p1 = new XYZ(coords[i].X, coords[i].Y, 0);
                XYZ p2 = new XYZ(coords[i + 1].X, coords[i + 1].Y, 0);
                curves.Add(CreateFlatLine(p1, p2));
            }
            return CurveLoop.Create(curves);
        }

        #endregion
    }
}

