' Lanza conciliar_bas.bat de forma OCULTA (sin ventana de consola).
' Lo invoca el protocolo conciliarbas:// ; recibe la URL como primer argumento.
' El "0" = ventana oculta ; False = no esperar a que termine.
Dim sh, arg
Set sh = CreateObject("WScript.Shell")
arg = ""
If WScript.Arguments.Count > 0 Then arg = WScript.Arguments(0)
sh.Run """C:\Agente\webapi\macro-conciliacion\conciliar_bas.bat"" """ & arg & """", 0, False
