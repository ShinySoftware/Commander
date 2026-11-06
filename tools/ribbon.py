#!/usr/bin/env python3
"""
Generates the ribbon markup in Views/MainPage.xaml from one command table:
  * <shiny:Ribbon>  - Shiny's ribbon, used on Windows + Linux
  * AppKitRibbon    - a ribbon-styled bar of plain layouts for the macOS AppKit head, where the Shiny
                      ribbon's body currently doesn't draw (it is built after the native view exists)
Run after editing RIBBON:  python3 tools/ribbon.py
"""
import re, pathlib

RIBBON = [
 ("Files","files","#3B82F6",[
   ("Transfer",[("Copy","copy","CopyCommand","L","F5"),("Move","arrow-right","MoveCommand","L","F6"),("Delete","trash","DeleteCommand","L","F8")]),
   ("Create",[("New Folder","folder","MakeDirectoryCommand","S","F7"),("New File","file","NewFileCommand","S","Shift+F4"),("Rename","edit","RenameCommand","S","F2")]),
   ("Open",[("View","eye","ViewCommand","L","F3"),("Edit","file-text","EditCommand","S","F4"),("Terminal","external-link","OpenTerminalCommand","S","Primary+O"),("Reveal","search","RevealInFileManagerCommand","S","Primary+E")]),
   ("Archive",[("Pack","download","PackCommand","S","Alt+F5"),("Unpack","upload","UnpackCommand","S","Alt+F9")]),
   ("Clipboard",[("Copy Paths","clipboard","CopyPathsToClipboardCommand","S","Primary+Shift+C"),("Name to Cmd","arrow-down","CopyNameToCommandLineCommand","S","Primary+Enter")]),
 ]),
 ("Selection","select","#8B5CF6",[
   ("Mark",[("Select All","check-circle","SelectAllCommand","L","Primary+A"),("Unselect All","x-circle","DeselectAllCommand","S","Primary+Shift+A"),("Invert","shuffle","InvertSelectionCommand","S","Primary+I")]),
   ("By pattern",[("Select Group…","filter","SelectGroupCommand","S","Primary+G"),("Unselect Group…","close","DeselectGroupCommand","S","Primary+Shift+G"),("Same Extension","tag","SelectSameExtensionCommand","S","Alt+Shift+E")]),
 ]),
 ("View","view","#14B8A6",[
   ("Panel",[("Reread","refresh","RefreshCommand","L","Primary+R"),("Hidden Files","eye","ToggleHiddenCommand","S","Primary+H"),("Folder Tree","folder-open","ToggleTreeCommand","S","Primary+F8"),("Branch View","expand","BranchViewCommand","S","Primary+B")]),
   ("Filter",[("Quick Filter","filter","QuickFilterCommand","L","Primary+F")]),
   ("Tabs",[("New Tab","plus","NewTabCommand","S","Primary+T"),("Close Tab","close","CloseTabCommand","S","Primary+W"),("Next Tab","chevron-right","NextTabCommand","S","Ctrl+Tab")]),
   ("Panels",[("Swap","repeat","SwapPanelsCommand","S","Primary+U"),("Target = Source","link","TargetEqualsSourceCommand","S","Primary+.")]),
 ]),
 ("Go","go","#22C55E",[
   ("History",[("Back","arrow-left","GoBackCommand","L","Alt+Left"),("Forward","arrow-right","GoForwardCommand","S","Alt+Right"),("Parent","arrow-up","GoParentCommand","S","Backspace")]),
   ("Places",[("Home","home","GoHomeCommand","L","Primary+Shift+H"),("Root","database","GoRootCommand","S","Primary+\\"),("Hotlist","star","HotlistCommand","S","Primary+D")]),
   ("Drives",[("Left Drive…","compass","DriveMenuLeftCommand","S","Alt+F1"),("Right Drive…","compass","DriveMenuRightCommand","S","Alt+F2"),("Other Panel","chevron-right","OpenInOtherPanelCommand","S","Primary+Right")]),
 ]),
 ("Tools","tools","#F97316",[
   ("Find",[("Find Files","search","FindCommand","L","Alt+F7")]),
   ("Batch",[("Multi-Rename","edit","MultiRenameCommand","L","Primary+M")]),
   ("Compare",[("Sync Dirs","refresh","SynchronizeCommand","L","Primary+Shift+S"),("Compare Files","activity","CompareFilesCommand","S","Primary+Shift+F2")]),
   ("App",[("Settings","settings","SettingsCommand","S","Primary+,"),("Keyboard Help","help","HelpCommand","S","F1")]),
 ]),
]

def esc(s): return s.replace("&","&amp;").replace('"',"&quot;")

# ---------------------------------------------------------------- Shiny Ribbon (Windows / Linux)
def shiny_item(text, icon, cmd, size, sc, color):
    sz = 'Large' if size=='L' else 'Small'
    px = 30 if size=='L' else 18
    return f'''                    <shiny:RibbonButton Text="{esc(text)}" Size="{sz}" Command="{{Binding {cmd}}}" Shortcut="{esc(sc)}" Tooltip="{esc(text)} ({esc(sc)})">
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
                <shiny:RibbonButton Text="Settings" Size="Small" Command="{Binding SettingsCommand}" Tooltip="Settings">
                    <shiny:RibbonButton.IconTemplate><DataTemplate><shiny:MotionIconView Icon="settings" Trigger="Appear" WidthRequest="16" HeightRequest="16" /></DataTemplate></shiny:RibbonButton.IconTemplate>
                </shiny:RibbonButton>
                <shiny:RibbonButton Text="Help" Size="Small" Command="{Binding HelpCommand}" Tooltip="Keyboard reference (F1)">
                    <shiny:RibbonButton.IconTemplate><DataTemplate><shiny:MotionIconView Icon="help" Trigger="Appear" WidthRequest="16" HeightRequest="16" /></DataTemplate></shiny:RibbonButton.IconTemplate>
                </shiny:RibbonButton>
            </shiny:Ribbon.QuickAccessItems>''']
    for tab,key,color,groups in RIBBON:
        o.append(f'            <shiny:RibbonTab Title="{tab}" Key="{key}">')
        for gt,items in groups:
            o.append(f'                <shiny:RibbonGroup Title="{esc(gt)}">')
            o += [shiny_item(*it, color) for it in items]
            o.append('                </shiny:RibbonGroup>')
        o.append('            </shiny:RibbonTab>')
    o.append('        </shiny:Ribbon>')
    return "\n".join(o)

# ---------------------------------------------------------------- AppKit ribbon (plain layouts)
def ak_large(text, icon, cmd, sc, color):
    return f'''                            <Border Style="{{StaticResource RibbonLarge}}" ToolTipProperties.Text="{esc(text)} ({esc(sc)})">
                                <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding {cmd}}}" /></Border.GestureRecognizers>
                                <VerticalStackLayout Spacing="3">
                                    <shiny:MotionIconView Icon="{icon}" Color="{color}" Trigger="Appear,Hover,Press" StrokeWidth="2" WidthRequest="28" HeightRequest="28" HorizontalOptions="Center" />
                                    <Label Text="{esc(text)}" Style="{{StaticResource RibbonText}}" HorizontalTextAlignment="Center" />
                                </VerticalStackLayout>
                            </Border>'''

def ak_small(text, icon, cmd, sc, color):
    return f'''                                <Border Style="{{StaticResource RibbonSmall}}" ToolTipProperties.Text="{esc(text)} ({esc(sc)})">
                                    <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding {cmd}}}" /></Border.GestureRecognizers>
                                    <HorizontalStackLayout Spacing="6">
                                        <shiny:MotionIconView Icon="{icon}" Color="{color}" Trigger="Appear,Hover,Press" StrokeWidth="2" WidthRequest="16" HeightRequest="16" VerticalOptions="Center" />
                                        <Label Text="{esc(text)}" Style="{{StaticResource RibbonText}}" VerticalTextAlignment="Center" />
                                    </HorizontalStackLayout>
                                </Border>'''

def appkit_ribbon():
    o=['''        <Grid x:Name="AppKitRibbon" Grid.Row="0" RowDefinitions="38,92" IsVisible="False" Style="{StaticResource Toolbar}">
            <Grid ColumnDefinitions="Auto,*,Auto" Padding="8,6,8,0" ColumnSpacing="10">
                <Border Style="{StaticResource RibbonAppButton}">
                    <Border.GestureRecognizers><TapGestureRecognizer Command="{Binding HelpCommand}" /></Border.GestureRecognizers>
                    <Label Text="ShinyCommander" TextColor="White" FontAttributes="Bold" FontSize="13" VerticalTextAlignment="Center" />
                </Border>
                <HorizontalStackLayout Grid.Column="1" Spacing="2">''']
    for tab,key,color,_ in RIBBON:
        o.append(f'''                    <Border Style="{{StaticResource RibbonTabHeader}}" BackgroundColor="{{Binding RibbonTab, Converter={{StaticResource RibbonTabBackground}}, ConverterParameter={key}}}">
                        <Border.GestureRecognizers><TapGestureRecognizer Command="{{Binding SelectRibbonTabCommand}}" CommandParameter="{key}" /></Border.GestureRecognizers>
                        <Label Text="{tab}" FontSize="13" VerticalTextAlignment="Center" TextColor="{{Binding RibbonTab, Converter={{StaticResource RibbonTabText}}, ConverterParameter={key}}}" />
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
                            <Label Grid.Row="1" Text="{esc(gt)}" Style="{{StaticResource RibbonGroupTitle}}" />
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
print("ribbon regenerated:", sum(len(i) for _,_,_,g in RIBBON for _,i in g), "commands")
