using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Zexus.Services;

namespace Zexus.Tools
{
    /// <summary>
    /// Helper class for working with Linked Models
    /// Provides coordinate transformation and Room lookup from Link models
    /// </summary>
    public static class LinkModelHelper
    {
        /// <summary>
        /// Get all RevitLinkInstances in the document
        /// </summary>
        public static List<RevitLinkInstance> GetAllLinkInstances(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(link => link.GetLinkDocument() != null)
                .ToList();
        }

        /// <summary>
        /// Get linked document by name (partial match)
        /// </summary>
        public static RevitLinkInstance GetLinkByName(Document doc, string linkName)
        {
            var links = GetAllLinkInstances(doc);
            return links.FirstOrDefault(link =>
                link.Name.IndexOf(linkName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Transform a point from host model coordinates to link model coordinates
        /// </summary>
        public static XYZ TransformPointToLink(XYZ hostPoint, RevitLinkInstance linkInstance)
        {
            var transform = linkInstance.GetTotalTransform();
            var inverseTransform = transform.Inverse;
            return inverseTransform.OfPoint(hostPoint);
        }

        /// <summary>
        /// Transform a point from link model coordinates to host model coordinates
        /// </summary>
        public static XYZ TransformPointToHost(XYZ linkPoint, RevitLinkInstance linkInstance)
        {
            var transform = linkInstance.GetTotalTransform();
            return transform.OfPoint(linkPoint);
        }

        /// <summary>
        /// Transform a BoundingBox from host coordinates to link coordinates
        /// </summary>
        public static BoundingBoxXYZ TransformBoundingBoxToLink(BoundingBoxXYZ hostBB, RevitLinkInstance linkInstance)
        {
            if (hostBB == null) return null;

            var transform = linkInstance.GetTotalTransform();
            var inverseTransform = transform.Inverse;

            var linkBB = new BoundingBoxXYZ();
            linkBB.Min = inverseTransform.OfPoint(hostBB.Min);
            linkBB.Max = inverseTransform.OfPoint(hostBB.Max);

            // Ensure Min < Max after transformation
            var minX = Math.Min(linkBB.Min.X, linkBB.Max.X);
            var minY = Math.Min(linkBB.Min.Y, linkBB.Max.Y);
            var minZ = Math.Min(linkBB.Min.Z, linkBB.Max.Z);
            var maxX = Math.Max(linkBB.Min.X, linkBB.Max.X);
            var maxY = Math.Max(linkBB.Min.Y, linkBB.Max.Y);
            var maxZ = Math.Max(linkBB.Min.Z, linkBB.Max.Z);

            linkBB.Min = new XYZ(minX, minY, minZ);
            linkBB.Max = new XYZ(maxX, maxY, maxZ);

            return linkBB;
        }

        /// <summary>
        /// Find the Room at a given point in the host model coordinates
        /// Searches both host document and all linked documents
        /// </summary>
        public static RoomInfo GetRoomAtPoint(Document doc, XYZ hostPoint)
        {
            // First try host document
            try
            {
                var hostRoom = doc.GetRoomAtPoint(hostPoint);
                if (hostRoom != null)
                {
                    return new RoomInfo
                    {
                        Room = hostRoom,
                        RoomId = RevitCompat.GetIdValue(hostRoom.Id),
                        RoomName = hostRoom.Name,
                        RoomNumber = hostRoom.Number,
                        Level = hostRoom.Level?.Name,
                        IsFromLink = false,
                        LinkName = null,
                        LinkInstance = null
                    };
                }
            }
            catch (Exception ex) { ZexusLogger.Warn($"LinkModelHelper: {ex.Message}"); }

            // Search in linked documents
            var linkInstances = GetAllLinkInstances(doc);

            foreach (var linkInstance in linkInstances)
            {
                try
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null) continue;

                    // Transform point to link coordinates
                    var linkPoint = TransformPointToLink(hostPoint, linkInstance);

                    var linkRoom = linkDoc.GetRoomAtPoint(linkPoint);
                    if (linkRoom != null)
                    {
                        return new RoomInfo
                        {
                            Room = linkRoom,
                            RoomId = RevitCompat.GetIdValue(linkRoom.Id),
                            RoomName = linkRoom.Name,
                            RoomNumber = linkRoom.Number,
                            Level = linkRoom.Level?.Name,
                            IsFromLink = true,
                            LinkName = linkInstance.Name,
                            LinkInstance = linkInstance
                        };
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"LinkModelHelper: {ex.Message}"); }
            }

            return null;
        }

        /// <summary>
        /// Find the Room containing an element (using element's bounding box center)
        /// </summary>
        public static RoomInfo GetRoomOfElement(Document doc, Element element)
        {
            // Try location point first
            var locPoint = element.Location as LocationPoint;
            if (locPoint != null)
            {
                var roomInfo = GetRoomAtPoint(doc, locPoint.Point);
                if (roomInfo != null) return roomInfo;
            }

            // Try bounding box center
            var bb = element.get_BoundingBox(null);
            if (bb != null)
            {
                var center = (bb.Min + bb.Max) / 2;
                return GetRoomAtPoint(doc, center);
            }

            return null;
        }

        /// <summary>
        /// Get all Rooms from all linked documents
        /// </summary>
        public static List<RoomInfo> GetAllRoomsFromLinks(Document doc)
        {
            var rooms = new List<RoomInfo>();
            var linkInstances = GetAllLinkInstances(doc);

            foreach (var linkInstance in linkInstances)
            {
                try
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null) continue;

                    var linkRooms = new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.Area > 0); // Only placed rooms

                    foreach (var room in linkRooms)
                    {
                        rooms.Add(new RoomInfo
                        {
                            Room = room,
                            RoomId = RevitCompat.GetIdValue(room.Id),
                            RoomName = room.Name,
                            RoomNumber = room.Number,
                            Level = room.Level?.Name,
                            IsFromLink = true,
                            LinkName = linkInstance.Name,
                            LinkInstance = linkInstance
                        });
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"LinkModelHelper: {ex.Message}"); }
            }

            return rooms;
        }

        /// <summary>
        /// Get elements from a linked document by category
        /// Returns elements with their host-transformed locations
        /// </summary>
        public static List<LinkedElementInfo> GetLinkedElements(
            Document doc,
            BuiltInCategory category,
            string linkNameFilter = null)
        {
            var results = new List<LinkedElementInfo>();
            var linkInstances = GetAllLinkInstances(doc);

            foreach (var linkInstance in linkInstances)
            {
                // Filter by link name if specified
                if (!string.IsNullOrEmpty(linkNameFilter) &&
                    linkInstance.Name.IndexOf(linkNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null) continue;

                    var elements = new FilteredElementCollector(linkDoc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType();

                    foreach (var elem in elements)
                    {
                        var bb = elem.get_BoundingBox(null);
                        XYZ hostLocation = null;
                        BoundingBoxXYZ hostBB = null;

                        if (bb != null)
                        {
                            var linkCenter = (bb.Min + bb.Max) / 2;
                            hostLocation = TransformPointToHost(linkCenter, linkInstance);

                            hostBB = new BoundingBoxXYZ();
                            hostBB.Min = TransformPointToHost(bb.Min, linkInstance);
                            hostBB.Max = TransformPointToHost(bb.Max, linkInstance);
                        }

                        results.Add(new LinkedElementInfo
                        {
                            Element = elem,
                            ElementId = RevitCompat.GetIdValue(elem.Id),
                            Name = elem.Name,
                            Category = elem.Category?.Name,
                            LinkInstance = linkInstance,
                            LinkName = linkInstance.Name,
                            HostLocation = hostLocation,
                            HostBoundingBox = hostBB
                        });
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"LinkModelHelper: {ex.Message}"); }
            }

            return results;
        }

        /// <summary>
        /// Check if two elements are in the same Room (considering linked Rooms)
        /// </summary>
        public static bool AreElementsInSameRoom(Document doc, Element elem1, Element elem2)
        {
            var room1 = GetRoomOfElement(doc, elem1);
            var room2 = GetRoomOfElement(doc, elem2);

            if (room1 == null || room2 == null) return false;

            // Compare by Room ID and Link name (in case same ID in different links)
            return room1.RoomId == room2.RoomId &&
                   room1.LinkName == room2.LinkName;
        }

        /// <summary>
        /// Get summary of all linked models
        /// </summary>
        public static List<LinkSummary> GetLinkSummary(Document doc)
        {
            var summaries = new List<LinkSummary>();
            var linkInstances = GetAllLinkInstances(doc);

            foreach (var linkInstance in linkInstances)
            {
                try
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    if (linkDoc == null) continue;

                    var roomCount = new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Count();

                    summaries.Add(new LinkSummary
                    {
                        LinkName = linkInstance.Name,
                        LinkInstance = linkInstance,
                        DocumentTitle = linkDoc.Title,
                        RoomCount = roomCount,
                        IsLoaded = true
                    });
                }
                catch
                {
                    summaries.Add(new LinkSummary
                    {
                        LinkName = linkInstance.Name,
                        LinkInstance = linkInstance,
                        IsLoaded = false
                    });
                }
            }

            return summaries;
        }
    }

    /// <summary>
    /// Information about a Room (from host or linked document)
    /// </summary>
    public class RoomInfo
    {
        public Room Room { get; set; }
        public long RoomId { get; set; }
        public string RoomName { get; set; }
        public string RoomNumber { get; set; }
        public string Level { get; set; }
        public bool IsFromLink { get; set; }
        public string LinkName { get; set; }
        public RevitLinkInstance LinkInstance { get; set; }

        public string DisplayName =>
            string.IsNullOrEmpty(RoomNumber) ? RoomName : $"{RoomNumber} - {RoomName}";
    }

    /// <summary>
    /// Information about an element from a linked document
    /// </summary>
    public class LinkedElementInfo
    {
        public Element Element { get; set; }
        public long ElementId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public RevitLinkInstance LinkInstance { get; set; }
        public string LinkName { get; set; }
        public XYZ HostLocation { get; set; }
        public BoundingBoxXYZ HostBoundingBox { get; set; }
    }

    /// <summary>
    /// Summary information about a linked model
    /// </summary>
    public class LinkSummary
    {
        public string LinkName { get; set; }
        public RevitLinkInstance LinkInstance { get; set; }
        public string DocumentTitle { get; set; }
        public int RoomCount { get; set; }
        public bool IsLoaded { get; set; }
    }
}
