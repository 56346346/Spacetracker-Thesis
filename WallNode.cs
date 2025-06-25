using System;
using System.Runtime.Versioning;

namespace SpaceTracker;

[SupportedOSPlatform("windows")]
public record WallNode(
    string Uid,
    long ElementId,
    long TypeId,
     string TypeName,
    string FamilyName,
    long LevelId,
    double X1,
    double Y1,
    double Z1,
    double X2,
    double Y2,
    double Z2,
    double HeightMm,
    double ThicknessMm,
     bool Structural,
       bool Structural,
    bool Flipped,
    double BaseOffsetMm,
    int LocationLine
);