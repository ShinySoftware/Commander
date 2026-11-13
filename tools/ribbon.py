#!/usr/bin/env python3
"""
Generates the ribbon markup in Views/MainPage.xaml from one command table:
  * <shiny:Ribbon>  - Shiny's ribbon, used on Windows + Linux
  * AppKitRibbon    - a ribbon-styled bar of plain layouts for the macOS AppKit head, where the Shiny
                      ribbon's body currently doesn't draw (it is built after the native view exists)
  * Services/CommandCatalog.g.cs - every command, for the customizable pane toolbar
Run after editing RIBBON:  python3 tools/ribbon.py

The text is localized: the table's English is what Strings.resx must hold for each key (Cmd_<command>,
Ribbon_Tab_<key>, Ribbon_Group_<title>) - the script lists keys missing from Strings.resx / .fr / .es.
"""
import re, pathlib

# every icon in the one neutral colour: the labels and groups do the telling apart
ICON = "{AppThemeBinding Light={StaticResource SubtleLight}, Dark={StaticResource SubtleDark}}"

RIBBON = [
 ("Files","files",ICON,[
   ("Transfer",[("Copy","copy","CopyCommand","L","F5"),("Move","arrow-right","MoveCommand","L","F6"),("Delete","trash","DeleteCommand","L","F8"),("Jobs","hourglass","ToggleJobsCommand","S","Primary+J")]),
   ("Create",[("New Folder","folder","MakeDirectoryCommand","S","F7"),("New File","file","NewFileCommand","S","Shift+F4"),("Rename","edit","RenameCommand","S","F2")]),
   ("Open",[("View","eye","ViewCommand","L","F3"),("Edit","file-text","EditCommand","S","F4"),("Terminal","external-link","OpenTerminalCommand","S","Primary+O"),("Reveal","search","RevealInFileManagerCommand","S","Primary+E")]),
   ("Archive",[("Pack","download","PackCommand","S","Alt+F5"),("Unpack","upload","UnpackCommand","S","Alt+F9")]),
   ("Clipboard",[("Copy Paths","clipboard","CopyPathsToClipboardCommand","S","Primary+Shift+C"),("Name to Cmd","arrow-down","CopyNameToCommandLineCommand","S","Primary+Enter")]),
 ]),
 ("Selection","select",ICON,[
   ("Mark",[("Select All","check-circle","SelectAllCommand","L","Primary+A"),("Unselect All","x-circle","DeselectAllCommand","S","Primary+Shift+A"),("Invert","shuffle","InvertSelectionCommand","S","Primary+I")]),
   ("By pattern",[("Select Group…","filter","SelectGroupCommand","S","Primary+G"),("Unselect Group…","close","DeselectGroupCommand","S","Primary+Shift+G"),("Same Extension","tag","SelectSameExtensionCommand","S","Alt+Shift+E")]),
 ]),
 ("View","view",ICON,[
   ("Panel",[("Reread","refresh","RefreshCommand","L","Primary+R"),("Hidden Files","eye","ToggleHiddenCommand","S","Primary+H"),("Branch View","expand","BranchViewCommand","S","Primary+B"),("Quick Filter","filter","QuickFilterCommand","S","Primary+F")]),
   ("Show",[("Thumbnails","image","ThumbnailViewCommand","L","Primary+Shift+F1"),("Details","menu","DetailsViewCommand","S","Primary+F2"),("Columns…","sort","ChooseColumnsCommand","S","Primary+Shift+K"),("Folder Tree","folder-open","ToggleTreeCommand","S","Primary+F8")]),
   ("Layout",[("Quick View","zoom-in","QuickViewCommand","L","Ctrl+Q"),("Stack Panels","collapse","TogglePanelLayoutCommand","S","Primary+Shift+L"),("Swap","repeat","SwapPanelsCommand","S","Primary+U"),("Target = Source","link","TargetEqualsSourceCommand","S","Primary+.")]),
   ("Tabs",[("New Tab","plus","NewTabCommand","S","Primary+T"),("Close Tab","close","CloseTabCommand","S","Primary+W"),("Next Tab","chevron-right","NextTabCommand","S","Ctrl+Tab")]),
 ]),
 ("Go","go",ICON,[
   ("History",[("Back","arrow-left","GoBackCommand","L","Alt+Left"),("Forward","arrow-right","GoForwardCommand","S","Alt+Right"),("Parent","arrow-up","GoParentCommand","S","Backspace")]),
   ("Places",[("Home","home","GoHomeCommand","L","Primary+Shift+H"),("Root","database","GoRootCommand","S","Primary+\\"),("Hotlist","star","HotlistCommand","S","Primary+D")]),
   ("Drives",[("Left Drive…","compass","DriveMenuLeftCommand","S","Alt+F1"),("Right Drive…","compass","DriveMenuRightCommand","S","Alt+F2"),("Other Panel","chevron-right","OpenInOtherPanelCommand","S","Primary+Right")]),
 ]),
 ("Tools","tools",ICON,[
   ("Find",[("Find Files","search","FindCommand","L","Alt+F7")]),
   ("Batch",[("Multi-Rename","edit","MultiRenameCommand","L","Primary+M")]),
   ("Compare",[("Sync Dirs","refresh","SynchronizeCommand","L","Primary+Shift+S"),("Compare Files","activity","CompareFilesCommand","S","Primary+Shift+F2")]),
   ("App",[("Settings","settings","SettingsCommand","S","Primary+,"),("Keyboard Help","help","HelpCommand","S","F1")]),
 ]),
]

def esc(s): return s.replace("&","&amp;").replace('"',"&quot;")

# --------------------------------------------------------------- localization keys
def cmd_key(cmd): return "Cmd_" + cmd[:-len("Command")]
def tab_key(key): return "Ribbon_Tab_" + key
def group_key(title): return "Ribbon_Group_" + re.sub(r"[^A-Za-z0-9]", "", title.title())
def tr(key): return "{l:Tr " + key + "}"
def tr_tip(key, sc):
    # the markup parser treats \ as an escape - "Primary+\\" must arrive doubled
    return "{l:Tr " + key + ", Suffix=' (" + esc(sc).replace("\\", "\\\\") + ")'}"

# ---------------------------------------------------------------- Shiny Ribbon (Windows / Linux)
def shiny_item(text, icon, cmd, size, sc, color):
    sz = 'Large' if size=='L' else 'Small'
    px = 30 if size=='L' else 18
    return f'''                    <shiny:RibbonButton Text="{tr(cmd_key(cmd))}" Size="{sz}" Command="{{Binding {cmd}}}" Shortcut="{esc(sc)}" Tooltip="{tr_tip(cmd_key(cmd), sc)}">
                        <shiny:RibbonButton.IconTemplate>
                            <DataTemplate><shiny:MotionIconView Icon="{icon}" Color="{color}" Trigger="Appear" StrokeWidth="2" WidthRequest="{px}" HeightRequest="{px}" /></DataTemplate>
                        </shiny:RibbonButton.IconTemplate>
                    </shiny:RibbonButton>'''

def shiny_ribbon():
    o=['''        <shiny:Ribbon x:Name="MainRibbon"
                      Grid.Row="0"
                      ApplicationButtonText="ShinyCommander"
                      ApplicationButtonCommand="{Binding HelpCommand}"
                      AccentColor="{StaticResource Accent}"
                      AllowCollapse="True"
                      ShowTooltips="True">
            <shiny:Ribbon.QuickAccessItems>
                <shiny:RibbonButton Text="{l:Tr Cmd_Settings}" Size="Small" Command="{Binding SettingsCommand}" Tooltip="{l:Tr Cmd_Settings}">
                    <shiny:RibbonButton.IconTemplate><DataTemplate><shiny:MotionIconView Icon="settings" Trigger="Appear" WidthRequest="16" HeightRequest="16" /></DataTemplate></shiny:RibbonButton.IconTemplate>
                </shiny:RibbonButton>
                <shiny:RibbonButton Text="{l:Tr Ribbon_Help}" Size="Small" Command="{Binding HelpCommand}" Tooltip="{l:Tr Cmd_Help, Suffix=' (F1)'}">
                    <shiny:RibbonButton.IconTemplate><DataTemplate><shiny:MotionIconView Icon="help" Trigger="Appear" WidthRequest="16" HeightRequest="16" /></DataTemplate></shiny:RibbonButton.IconTemplate>
                </shiny:RibbonButton>
            </shiny:Ribbon.QuickAccessItems>''']
    for tab,key,color,groups in RIBBON:
        o.append(f'            <shiny:RibbonTab Title="{tr(tab_key(key))}" Key="{key}">')
        for gt,items in groups:
            o.append(f'                <shiny:RibbonGroup Title="{tr(group_key(gt))}">')
            o += [shiny_item(*it, color) for it in items]
            o.append('                </shiny:RibbonGroup>')
        o.append('            </shiny:RibbonTab>')
    o.append('        </shiny:Ribbon>')
    return "\n".join(o)

# ---------------------------------------------------------------- AppKit ribbon (plain layouts)
def ak_large(text, icon, cmd, sc, color):
    return f'''                            <Border Style="{{StaticResource RibbonLarge}}" AutomationId="ribbon-{cmd[:-7]}" ToolTipProperties.Text="{tr_tip(cmd_key(cmd), sc)}">
                                <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding {cmd}}}" /></Border.GestureRecognizers>
                                <VerticalStackLayout Spacing="3">
                                    <shiny:MotionIconView Icon="{icon}" Color="{color}" Trigger="Appear,Hover,Press" StrokeWidth="2" WidthRequest="28" HeightRequest="28" HorizontalOptions="Center" />
                                    <Label Text="{tr(cmd_key(cmd))}" Style="{{StaticResource RibbonText}}" HorizontalTextAlignment="Center" />
                                </VerticalStackLayout>
                            </Border>'''

def ak_small(text, icon, cmd, sc, color):
    return f'''                                <Border Style="{{StaticResource RibbonSmall}}" AutomationId="ribbon-{cmd[:-7]}" ToolTipProperties.Text="{tr_tip(cmd_key(cmd), sc)}">
                                    <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding {cmd}}}" /></Border.GestureRecognizers>
                                    <HorizontalStackLayout Spacing="6">
                                        <shiny:MotionIconView Icon="{icon}" Color="{color}" Trigger="Appear,Hover,Press" StrokeWidth="2" WidthRequest="16" HeightRequest="16" VerticalOptions="Center" />
                                        <Label Text="{tr(cmd_key(cmd))}" Style="{{StaticResource RibbonText}}" VerticalTextAlignment="Center" />
                                    </HorizontalStackLayout>
                                </Border>'''

def appkit_ribbon():
    o=['''        <Grid x:Name="AppKitRibbon" Grid.Row="0" RowDefinitions="38,92" IsVisible="False" Style="{StaticResource Toolbar}">
            <Grid ColumnDefinitions="Auto,*,Auto" Padding="8,6,8,0" ColumnSpacing="10">
                <Border Style="{StaticResource RibbonAppButton}">
                    <Border.GestureRecognizers><TapGestureRecognizer Command="{Binding HelpCommand}" /></Border.GestureRecognizers>
                    <Label Text="ShinyCommander" TextColor="{StaticResource Accent}" FontAttributes="Bold" FontSize="13" VerticalTextAlignment="Center" />
                </Border>
                <HorizontalStackLayout Grid.Column="1" Spacing="2">''']
    for tab,key,color,_ in RIBBON:
        o.append(f'''                    <Border Style="{{StaticResource RibbonTabHeader}}" AutomationId="ribbon-tab-{key}" BackgroundColor="{{Binding RibbonTab, Converter={{StaticResource RibbonTabBackground}}, ConverterParameter={key}}}">
                        <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding SelectRibbonTabCommand}}" CommandParameter="{key}" /></Border.GestureRecognizers>
                        <Label Text="{tr(tab_key(key))}" FontSize="13" VerticalTextAlignment="Center" TextColor="{{Binding RibbonTab, Converter={{StaticResource RibbonTabText}}, ConverterParameter={key}}}" />
                    </Border>''')
    o.append('''                </HorizontalStackLayout>
                <HorizontalStackLayout Grid.Column="2" Spacing="4">
                    <shiny:MotionIconView Icon="settings" Trigger="Hover,Press" Command="{Binding SettingsCommand}" WidthRequest="18" HeightRequest="18" VerticalOptions="Center" />
                    <shiny:MotionIconView Icon="help" Trigger="Hover,Press" Command="{Binding HelpCommand}" WidthRequest="18" HeightRequest="18" VerticalOptions="Center" Margin="6,0,0,0" />
                </HorizontalStackLayout>
            </Grid>
            <Border Grid.Row="1" StrokeThickness="0" Padding="8,4" BackgroundColor="{AppThemeBinding Light={StaticResource PanelLight}, Dark={StaticResource PanelDark}}">
                <Grid>''')
    for tab,key,color,groups in RIBBON:
        o.append(f'''                    <HorizontalStackLayout Spacing="0" IsVisible="{{Binding RibbonTab, Converter={{StaticResource RibbonTabVisible}}, ConverterParameter={key}}}">''')
        for gi,(gt,items) in enumerate(groups):
            if gi>0:
                o.append('                        <BoxView Style="{StaticResource RibbonDivider}" />')
            o.append('''                        <Grid RowDefinitions="*,Auto" Padding="4,0">
                            <HorizontalStackLayout Spacing="2">''')
            col=[]
            def flush():
                if col:
                    o.append('                                <VerticalStackLayout Spacing="0" VerticalOptions="Center">')
                    o.extend(col); col.clear()
                    o.append('                                </VerticalStackLayout>')
            for (text,icon,cmd,size,sc) in items:
                if size=='L':
                    flush()
                    o.append(ak_large(text,icon,cmd,sc,color))
                else:
                    col.append(ak_small(text,icon,cmd,sc,color))
                    if len(col)==3: flush()
            flush()
            o.append(f'''                            </HorizontalStackLayout>
                            <Label Grid.Row="1" Text="{tr(group_key(gt))}" Style="{{StaticResource RibbonGroupTitle}}" />
                        </Grid>''')
        o.append('                    </HorizontalStackLayout>')
    o.append('''                </Grid>
            </Border>
        </Grid>''')
    return "\n".join(o)

root = pathlib.Path(__file__).resolve().parent.parent / "src/ShinyCommander.Shared/Views/MainPage.xaml"
s = root.read_text()
block = "        <!-- RIBBON:BEGIN (generated by tools/ribbon.py - edit the table there) -->\n" + shiny_ribbon() + "\n\n" + appkit_ribbon() + "\n        <!-- RIBBON:END -->"
if "RIBBON:BEGIN" in s:
    s = re.sub(r"        <!-- RIBBON:BEGIN.*?RIBBON:END -->", lambda m: block, s, flags=re.S)
else:
    s = re.sub(r"        <shiny:Ribbon x:Name=\"MainRibbon\".*?</shiny:Ribbon>", lambda m: block, s, flags=re.S)
root.write_text(s)
# ---------------------------------------------------------------- command catalog (pane toolbar)
def cs(v): return '"' + v.replace('\\', '\\\\').replace('"', '\\"') + '"'
entries, cases = [], []
for tab,tkey,_,groups in RIBBON:
    for _,items in groups:
        for text,icon,cmd,_,sc in items:
            key = cmd[:-len("Command")]
            entries.append(f"        new({cs(key)}, {cs(icon)}, {cs(sc)}, {cs(tkey)}),")
            cases.append(f"        {cs(key)} => vm.{cmd},")
catalog = pathlib.Path(__file__).resolve().parent.parent / "src/ShinyCommander.Shared/Services/CommandCatalog.g.cs"
catalog.write_text("""// <auto-generated> by tools/ribbon.py from the ribbon table - edit the table there, then rerun it. </auto-generated>
namespace ShinyCommander.Services;

public static partial class CommandCatalog
{
    /// <summary>Every command, in ribbon order.</summary>
    public static IReadOnlyList<AppCommand> All { get; } =
    [
""" + "\n".join(entries) + """
    ];

    /// <summary>The command behind a key, on the main view model; null for a key that no longer exists.</summary>
    public static System.Windows.Input.ICommand? Resolve(MainViewModel vm, string key) => key switch
    {
""" + "\n".join(cases) + """
        _ => null
    };
}
""")

print("ribbon regenerated:", sum(len(i) for _,_,_,g in RIBBON for _,i in g), "commands")

# ---------------------------------------------------------------- check the strings
expected = {"Ribbon_Help": "Help"}
for tab,key,_,groups in RIBBON:
    expected[tab_key(key)] = tab
    for gt,items in groups:
        expected[group_key(gt)] = gt
        for text,_,cmd,_,_ in items:
            expected[cmd_key(cmd)] = text

import xml.etree.ElementTree as ET
res = pathlib.Path(__file__).resolve().parent.parent / "src/ShinyCommander.Shared/Resources/Strings"
for name in ["Strings.resx", "Strings.fr.resx", "Strings.es.resx"]:
    have = {d.get("name"): d.findtext("value") for d in ET.parse(res / name).getroot().iter("data")}
    missing = [k for k in expected if k not in have]
    wrong = [k for k in expected if name == "Strings.resx" and k in have and have[k] != expected[k]]
    if missing: print(f"{name}: missing {', '.join(missing)}")
    if wrong: print(f"{name}: differs from the table: {', '.join(wrong)}")

if "--keys" in __import__("sys").argv:
    import json
    print(json.dumps(expected, ensure_ascii=False, indent=1))
