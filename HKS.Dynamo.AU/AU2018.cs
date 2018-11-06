using Autodesk.Revit.UI;
using DynamoServices;
using Revit.GeometryConversion;
using RevitServices.Persistence;
using RevitServices.Transactions;
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.DesignScript.Runtime;
using dg = Autodesk.DesignScript.Geometry;
using rdb = Autodesk.Revit.DB;
using re = Revit.Elements;


namespace HKS.DynamoZT.AU
{
    [IsVisibleInDynamoLibrary(true)]
    public static class Wall
    {
        private static void DeleteWall(rdb.Wall wall, bool transNeeded)
        {
            if(transNeeded)
                TransactionManager.Instance.EnsureInTransaction(DocumentManager.Instance.CurrentDBDocument);

            if(wall != null)
                DocumentManager.Instance.CurrentDBDocument.Delete(wall.Id);

            if (transNeeded)
                TransactionManager.Instance.TransactionTaskDone();
        }

        [RegisterForTrace]
        public static re.Element WallByProfile(List<dg.PolyCurve> closedProfiles, re.WallType wallType, re.Level level)
        {
            rdb.Document doc = DocumentManager.Instance.CurrentDBDocument;

            // Try to get a wall from trace
            var wallElem = ElementBinder.GetElementFromTrace<rdb.Wall>(doc);

            dg.PolyCurve closedProfile = closedProfiles[0];
            if (!closedProfile.IsClosed || !closedProfile.IsPlanar)
            {
                DeleteWall(wallElem, true);
                return null;
            }

            // Verify the wall profile is vertical
            dg.Plane basePlane = closedProfile.BasePlane();
            if (Math.Abs(basePlane.Normal.Z) > 0.0001)
            {
                DeleteWall(wallElem, true);
                return null;
            }

            // Convert Polycurve segments to a list of Revit curves;
            List<rdb.Curve> rCrvs = new List<rdb.Curve>();
            foreach (dg.PolyCurve pCrv in closedProfiles)
            {
                List<dg.Curve> dCrvs = pCrv.Curves().ToList();
                foreach (dg.Curve dCrv in dCrvs)
                {
                    rdb.Curve rCrv = dCrv.ToRevitType();
                    rCrvs.Add(rCrv);
                }
            }
            

            TransactionManager.Instance.EnsureInTransaction(doc);
            DeleteWall(wallElem, false);

            // Build a wall
            try
            {
                rdb.Wall w = rdb.Wall.Create(doc, rCrvs, new rdb.ElementId(wallType.Id), new rdb.ElementId(level.Id), false);
                re.Wall rWall = re.ElementWrapper.ToDSType(w, true) as re.Wall;
                TransactionManager.Instance.TransactionTaskDone();
                ElementBinder.CleanupAndSetElementForTrace(doc, w);
                return rWall;
            }
            catch (Exception ex)
            {
                TransactionManager.Instance.TransactionTaskDone();
                
                ElementBinder.CleanupAndSetElementForTrace(doc, null);
            }
            return null;
        }

        private static bool VerifyRect(dg.PolyCurve crv)
        {
            if (crv == null)
                return false;

            if (!crv.IsClosed)
                return false;

            dg.Curve[] crvs = crv.Curves();
            if (crv.NumberOfCurves != 4)
                return false;

            dg.Vector v0 = dg.Vector.ByTwoPoints(crvs[0].StartPoint, crvs[0].EndPoint);
            dg.Vector v1 = dg.Vector.ByTwoPoints(crvs[1].StartPoint, crvs[1].EndPoint);
            dg.Vector v2 = dg.Vector.ByTwoPoints(crvs[2].StartPoint, crvs[2].EndPoint);
            dg.Vector v3 = dg.Vector.ByTwoPoints(crvs[3].StartPoint, crvs[3].EndPoint);

            if (Math.Abs(v0.Length - crvs[0].Length) > 0.001 ||
                Math.Abs(v1.Length - crvs[1].Length) > 0.001 ||
                Math.Abs(v2.Length - crvs[2].Length) > 0.001 ||
                Math.Abs(v3.Length - crvs[3].Length) > 0.001)
                return false;

            // verify the angles
            double a0 = v0.Reverse().AngleWithVector(v1);
            double a1 = v1.Reverse().AngleWithVector(v2);
            double a2 = v2.Reverse().AngleWithVector(v3);
            double a3 = v3.Reverse().AngleWithVector(v0);
            double deg90 = 90; //Math.PI * 0.5;
            if (Math.Abs(a0 - deg90) > 0.001 || Math.Abs(a1 - deg90) > 0.001 || Math.Abs(a2 - deg90) > 0.001 || Math.Abs(a3 - deg90) > 0.001)
                return false;

            return true;
        }

        private static bool GetRectCorners(dg.PolyCurve crv, out rdb.XYZ corner0, out rdb.XYZ corner1)
        {
            if (crv.NumberOfCurves != 4)
            {
                corner0 = null;
                corner1 = null;
                return false;
            }

            List<rdb.XYZ> points = new List<rdb.XYZ>();
            foreach(dg.Curve c in crv.Curves())
            {
                points.Add(c.StartPoint.ToXyz());
            }
            points.Sort((x,y) => x.Z.CompareTo(y.Z));

            corner0 = points[0];
            var vect2 = points[2] - corner0;
            if (vect2.AngleTo(rdb.XYZ.BasisZ) > 0.01)
            {
                corner1 = points[2];
                return true;
            }

            corner1 = points[3];
            return true;
        }

        [RegisterForTrace]
        public static re.Element CreateWallOpening(re.Wall wall, dg.PolyCurve polyCrv)
        {
            try
            {

            // Try to get a wall from trace
            rdb.Document doc = DocumentManager.Instance.CurrentDBDocument;
            var openingElem = ElementBinder.GetElementFromTrace<rdb.Element>(doc);
            rdb.Opening opening = null;
            if (openingElem != null && openingElem.Id.IntegerValue != (int)rdb.BuiltInCategory.OST_SWallRectOpening)
                opening = openingElem as rdb.Opening;

            if (!VerifyRect(polyCrv))
            {
                if (opening != null)
                {
                    TransactionManager.Instance.EnsureInTransaction(doc);
                    doc.Delete(opening.Id);
                    TransactionManager.Instance.TransactionTaskDone();
                }
                TaskDialog.Show("Test", "VerifyRect failed");
                return null;
            }

            // Find the two corner points.
            if (!GetRectCorners(polyCrv, out rdb.XYZ corner0, out rdb.XYZ corner1))
            {
                if (opening != null)
                {
                    TransactionManager.Instance.EnsureInTransaction(doc);
                    doc.Delete(opening.Id);
                    TransactionManager.Instance.TransactionTaskDone();
                }
                TaskDialog.Show("Test", "GetRectCorners failed");
                    return null;
            }

            if (corner0 == null || corner1 == null)
            {
                if (opening != null)
                {
                    TransactionManager.Instance.EnsureInTransaction(doc);
                    doc.Delete(opening.Id);
                    TransactionManager.Instance.TransactionTaskDone();
                }
                TaskDialog.Show("Test", "one of the corners is null");
                return null;
            }

            TransactionManager.Instance.EnsureInTransaction(doc);
            // Purge the original element
            if (opening != null)
                doc.Delete(opening.Id);

            // Build a new opening;
            try
            {
                rdb.Wall w = wall.InternalElement as rdb.Wall;
                rdb.Opening o = doc.Create.NewOpening(w, corner0, corner1);
                re.Element rOpening = re.ElementWrapper.ToDSType(o, true) as re.Element;
                TransactionManager.Instance.TransactionTaskDone();
                ElementBinder.CleanupAndSetElementForTrace(doc, o);
                return rOpening;
            }
            catch (Exception ex)
            {
                TransactionManager.Instance.TransactionTaskDone();
                TaskDialog.Show("CreateError", ex.ToString());
                ElementBinder.CleanupAndSetElementForTrace(doc, null);
            }
            }
            catch (Exception e)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Test", "Error:\n" + e.ToString());
                throw;
            }
            return null;
        }
    }
}
