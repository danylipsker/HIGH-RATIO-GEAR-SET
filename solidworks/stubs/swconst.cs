namespace SolidWorks.Interop.swconst
{
    public enum swUserPreferenceStringValue_e { swDefaultTemplatePart = 8, swDefaultTemplateAssembly = 9 }
    public enum swStartConditions_e { swStartSketchPlane = 0, swStartSurface = 1, swStartVertex = 2, swStartOffset = 3 }
    public enum swEndConditions_e { swEndCondBlind = 0 }
    public enum swAddComponentConfigOptions_e { swAddComponentConfigOptions_CurrentSelectedConfig = 0 }
    public enum swDocumentTypes_e { swDocPART = 1, swDocASSEMBLY = 2, swDocDRAWING = 3 }
    public enum swOpenDocOptions_e { swOpenDocOptions_Silent = 1 }
    public enum swSaveAsVersion_e { swSaveAsCurrentVersion = 0 }
    public enum swSaveAsOptions_e { swSaveAsOptions_Silent = 1 }
    public enum swCustomInfoType_e { swCustomInfoText = 30 }
    public enum swCustomPropertyAddOption_e { swCustomPropertyReplaceValue = 1 }
    public enum swBodyType_e { swSolidBody = 0 }
    public enum swMateType_e { swMateCOINCIDENT = 0, swMateCONCENTRIC = 1, swMatePARALLEL = 3, swMateGEAR = 10 }
    public enum swMateAlign_e { swMateAlignALIGNED = 0, swMateAlignANTI_ALIGNED = 1, swMateAlignCLOSEST = 2 }
    public enum swCommandItemType_e { swMenuItem = 1, swToolbarItem = 2 }
    public enum swCommandTabButtonTextDisplay_e { swCommandTabButton_TextBelow = 2 }
    public enum swMessageBoxIcon_e { swMbWarning = 1, swMbInformation = 2 }
    public enum swMessageBoxBtn_e { swMbOk = 2 }
}

namespace SolidWorks.Interop.swconst { public enum swSketchSegments_e { swSketchLINE = 0, swSketchARC = 1, swSketchELLIPSE = 2, swSketchSPLINE = 3, swSketchTEXT = 4, swSketchPARABOLA = 5 } }

namespace SolidWorks.Interop.swconst { public enum swConstrainedStatus_e { swUnknownConstraint = 0, swUnderConstrained = 1, swFullyConstrained = 2, swOverConstrained = 3, swNoSolution = 4, swInvalidSolution = 5 } }
