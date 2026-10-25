// Stubs mirroring the members of SolidWorks.Interop.sldworks used by the Gear Set add-in (compile check only).
namespace SolidWorks.Interop.sldworks
{
    public interface ISldWorks
    {
        string GetUserPreferenceStringValue(int userPreference);
        object NewDocument(string templateName, int paperSize, double width, double height);
        void CloseDoc(string name);
        ModelDoc2 OpenDoc6(string fileName, int type, int options, string configuration, ref int errors, ref int warnings);
        bool SetAddinCallbackInfo2(int moduleHandle, object addInCallbacks, int cookie);
        CommandManager GetCommandManager(int cookie);
        string RevisionNumber();
        int SendMsgToUser2(string message, int icon, int buttons);
        bool Visible { get; set; }
        void ExitApp();
        object ActiveDoc { get; }
    }
    public interface IModelDoc2
    {
        SketchManager SketchManager { get; }
        FeatureManager FeatureManager { get; }
        ModelDocExtension Extension { get; }
        object FirstFeature();
        object SelectionManager { get; }
        void ClearSelection2(bool all);
        string GetTitle();
        bool ForceRebuild3(bool topOnly);
        void ShowNamedView2(string name, int viewId);
        void ViewZoomtofit2();
        EquationMgr GetEquationMgr();
    }
    public interface ModelDoc2 : IModelDoc2 { }
    public interface ISketchManager
    {
        bool InsertSketch(bool updateEditRebuild);
        bool AddToDB { get; set; }
        bool DisplayWhenAdded { get; set; }
        Sketch ActiveSketch { get; }
        SketchSegment CreateCircleByRadius(double xc, double yc, double zc, double radius);
        SketchSegment CreateArc(double xc, double yc, double zc, double xs, double ys, double zs, double xe, double ye, double ze, short direction);
        SketchSegment CreateLine(double x1, double y1, double z1, double x2, double y2, double z2);
        SketchSegment CreateSpline2(object pointData, bool simulateNaturalEnds);
    }
    public interface SketchManager : ISketchManager { }
    public interface ISketch { }
    public interface Sketch : ISketch { }
    public interface IFeature { bool Select2(bool append, int mark); string GetTypeName2(); object GetNextFeature(); }
    public interface Feature : IFeature { }
    public interface IFeatureManager
    {
        Feature FeatureExtrusion3(bool sd, bool flip, bool dir, int t1, int t2, double d1, double d2, bool dchk1, bool dchk2, bool ddir1, bool ddir2,
            double dang1, double dang2, bool offsetReverse1, bool offsetReverse2, bool translateSurface1, bool translateSurface2,
            bool merge, bool useFeatScope, bool useAutoSelect, int t0, double startOffset, bool flipStartOffset);
    }
    public interface FeatureManager : IFeatureManager { }
    public interface ISketchSegment { }
    public interface SketchSegment : ISketchSegment { }
    public interface ISketchArc { object GetNormalVector(); }
    public interface SketchArc : ISketchArc { }
    public interface IPartDoc { object GetBodies2(int bodyType, bool visibleOnly); }
    public interface IBody2 { object GetBodyBox(); object GetFaces(); }
    public interface Body2 : IBody2 { }
    public interface IAssemblyDoc
    {
        Component2 AddComponent5(string compName, int configOption, string newConfigName, bool useConfigForPartReferences, string existingConfigName, double x, double y, double z);
        object AddMate5(int mateTypeFromEnum, int alignFromEnum, bool flip, double distance, double distanceAbsUpperLimit, double distanceAbsLowerLimit,
            double gearRatioNumerator, double gearRatioDenominator, double angle, double angleAbsUpperLimit, double angleAbsLowerLimit,
            bool forPositioningOnly, bool lockRotation, int widthMateOption, out int errorStatus);
    }
    public interface IComponent2 { string Name2 { get; } object GetBodies3(int bodyType, out object bodyInfo); object FirstFeature(); }
    public interface IEntity { bool Select4(bool append, SelectData data); }
    public interface Entity : IEntity { }
    public interface ISelectData { int Mark { get; set; } }
    public interface SelectData : ISelectData { }
    public interface ISelectionMgr { object CreateSelectData(); }
    public interface IFace2 : IEntity { object GetSurface(); object GetBox(); }
    public interface ISurface { bool IsCylinder(); bool IsPlane(); object CylinderParams { get; } }
    public interface Component2 : IComponent2 { }
    public interface IModelDocExtension
    {
        CustomPropertyManager get_CustomPropertyManager(string configuration);
        bool SaveAs3(string name, int version, int options, object exportData, object advancedSaveAsOptions, ref int errors, ref int warnings);
    }
    public interface ModelDocExtension : IModelDocExtension { }
    public interface ICustomPropertyManager { int Add3(string fieldName, int fieldType, string fieldValue, int overwriteExisting); }
    public interface CustomPropertyManager : ICustomPropertyManager { }
    public interface IEquationMgr { int Add2(int index, string equation, bool solve); }
    public interface EquationMgr : IEquationMgr { }
    public interface ICommandManager
    {
        CommandGroup CreateCommandGroup2(int userId, string title, string toolTip, string hint, int position, bool ignorePreviousVersion, ref int errors);
        CommandTab GetCommandTab(int documentType, string tabName);
        bool RemoveCommandTab(CommandTab tab);
        CommandTab AddCommandTab(int documentType, string tabName);
        int RemoveCommandGroup2(int userId, bool runtimeOnly);
    }
    public interface CommandManager : ICommandManager { }
    public interface ICommandGroup
    {
        object IconList { set; }
        object MainIconList { set; }
        int AddCommandItem2(string name, int position, string hintString, string toolTip, int imageListIndex, string callbackFunction, string enableMethod, int userId, int menuTBOption);
        bool HasToolbar { set; }
        bool HasMenu { set; }
        bool Activate();
        int get_CommandID(int index);
    }
    public interface CommandGroup : ICommandGroup { }
    public interface ICommandTab { CommandTabBox AddCommandTabBox(); }
    public interface CommandTab : ICommandTab { }
    public interface ICommandTabBox { bool AddCommands(object commandIds, object textDisplayStyles); }
    public interface CommandTabBox : ICommandTabBox { }
}
