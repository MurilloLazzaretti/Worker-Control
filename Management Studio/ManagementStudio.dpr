program ManagementStudio;

uses
  Vcl.Forms,
  uMain in 'src\uMain.pas' {FrmMain},
  Vcl.Themes,
  Vcl.Styles,
  ManagementStudio.Config in 'src\ManagementStudio.Config.pas',
  ManagementStudio.Worker in 'src\ManagementStudio.Worker.pas',
  ManagementStudio.ServiceManager in 'src\ManagementStudio.ServiceManager.pas';

{$R *.res}

begin
  ReportMemoryLeaksOnShutdown := True;
  Application.Initialize;
  Application.MainFormOnTaskbar := True;
  TStyleManager.TrySetStyle('Amethyst Kamri');
  Application.CreateForm(TFrmMain, FrmMain);
  Application.Run;
end.
