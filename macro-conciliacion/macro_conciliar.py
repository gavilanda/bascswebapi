# -*- coding: utf-8 -*-
# =============================================================================
#  MACRO Conciliación BAS (pywinauto) — versión completa
#  Cadena:
#   [Conciliación bancaria] tipear cuenta (011) -> esperar que habilite "Archivo"
#     -> click "Archivo"
#   [Conciliación por archivo] click "&Archivos"
#   [Archivos para conciliar] click "&Agregar"
#   [Importación del archivo de conciliación] cargar Archivo + Descripción -> "&Importar"
#   volver a [Archivos para conciliar] -> "&Ok" -> queda en [Conciliación por archivo]
#
#  Requiere:  pip install pywinauto   y correr COMO ADMINISTRADOR (BAS es admin).
#  ANTES: en BAS abrí "Conciliación bancaria" (Procesos > Conciliación Bancaria >
#         Ingreso). El menú lo automatizamos en una segunda etapa.
#
#  Backends: UIA para el form embebido (auto_id estables); win32 para los diálogos.
# =============================================================================
import sys, time, datetime, re, os, glob
try:
    from pywinauto import Desktop
except ImportError:
    print("Falta pywinauto. Corré:  pip install pywinauto"); sys.exit(1)

# ---- Config ----
CARPETA = r"C:\conciliacion"      # donde el portal deja CONC_<EMPRESA>.txt + .info
FECHA = datetime.date.today().strftime("%d/%m/%Y")
IMPORTAR = True                   # True = importa + Ok final; False = solo llena (prueba)

# El .info trae el Nº de cuenta BANCARIA (el que el portal le pasa a Credicoop, ej.
# "00440199559"). La pantalla de conciliación de BAS usa un CÓDIGO INTERNO propio
# (ej. "011"), distinto. Idealmente el portal ya escribe ese código en el .info
# (clave 'cuentaBas', configurado por empresa en la intranet). Esta tabla local queda
# SOLO como RESPALDO por si el .info no lo trae:
CUENTAS_BAS = {
    "00440199559": "011",         # BARK — Credicoop
    # "<nro_cuenta_banco>": "<codigo_en_BAS>",   # respaldo para otras cuentas
}
# Empresa: 1er argumento (ej. "python macro_conciliar.py XARDO"); si no, se toma el
# .info MÁS RECIENTE de la carpeta (o sea, la última exportación hecha en el portal).


def _leer_info(ruta):
    """Lee el CONC_<empresa>.info (líneas key=value) y devuelve un dict. {} si no existe."""
    d = {}
    try:
        with open(ruta, encoding="utf-8-sig") as f:
            for ln in f:
                if "=" in ln:
                    k, v = ln.split("=", 1)
                    d[k.strip()] = v.strip()
    except Exception:
        pass
    return d


def _elegir_info(carpeta, empresa_arg):
    """Devuelve la ruta del .info a usar: por empresa (arg) o el más reciente. None si no hay."""
    if empresa_arg:
        ruta = os.path.join(carpeta, "CONC_%s.info" % empresa_arg)
        return ruta if os.path.exists(ruta) else None
    infos = glob.glob(os.path.join(carpeta, "CONC_*.info"))
    return max(infos, key=os.path.getmtime) if infos else None


# La empresa llega por argumento. Puede venir "pelada" (BARK) o como URL del protocolo
# (conciliarbas://BARK  /  conciliarbas:BARK/). Normalizamos ambos casos.
_arg = (sys.argv[1].strip() if len(sys.argv) > 1 else "")
if _arg.lower().startswith("conciliarbas:"):
    _arg = _arg.split(":", 1)[1]        # saca "conciliarbas:"
_arg = _arg.strip("/").strip()          # saca "//" y "/" sobrantes
_empresa_arg = _arg.upper()
INFO = _elegir_info(CARPETA, _empresa_arg)
if INFO is None:
    _q = ("CONC_%s.info" % _empresa_arg) if _empresa_arg else "ningún CONC_*.info"
    print("No encontré %s en %s. Exportá primero desde el portal (Bco/Conciliación)." % (_q, CARPETA))
    sys.exit(1)
_info = _leer_info(INFO)
EMPRESA = _info.get("empresa", "")
CUENTA_BANCO = _info.get("cuenta", "")          # Nº de cuenta bancaria (del .info)
# Código interno de BAS: primero lo que ya trae el portal en el .info; si no, la tabla local.
CUENTA = _info.get("cuentaBas", "") or CUENTAS_BAS.get(CUENTA_BANCO, "")
ARCHIVO = INFO[:-5] + ".txt"      # CONC_<EMPRESA>.txt  (mismo nombre que el .info)
DESCRIPCION = "CREDICOOP - " + FECHA
if not CUENTA_BANCO:
    print("El %s no trae 'cuenta='. Re-exportá desde el portal (versión nueva)." % os.path.basename(INFO))
    sys.exit(1)
if not CUENTA:
    print("La cuenta bancaria %s (empresa %s) no está mapeada al código de BAS." % (CUENTA_BANCO, EMPRESA or "?"))
    print("Agregá la línea  \"%s\": \"<codigo_en_BAS>\",  en CUENTAS_BAS (arriba del macro)." % CUENTA_BANCO)
    sys.exit(1)
if not os.path.exists(ARCHIVO):
    print("Falta el TXT %s (está el .info pero no el .txt)." % ARCHIVO)
    sys.exit(1)
# ----------------

uia = Desktop(backend="uia")
win = Desktop(backend="win32")


def paso(msg): print(">>", msg)


def _listar_ventanas():
    """Diagnóstico: imprime el título y clase de TODAS las ventanas top-level (ambos backends)."""
    for nombre, d in (("win32", win), ("uia", uia)):
        print("   --- ventanas top-level (%s):" % nombre)
        try:
            vistas = set()
            for w in d.windows():
                try:
                    t = (w.window_text() or "").strip()
                    c = w.class_name() or ""
                    if t and (t, c) not in vistas:
                        vistas.add((t, c))
                        print("       [%s]  '%s'" % (c, t))
                except Exception:
                    pass
        except Exception as e:
            print("       ERROR:", e)


def buscar_dialogo(patron, timeout=60):
    """Busca (case-insensitive) una ventana top-level WIN32 cuyo título matchee `patron`.
    Devuelve un WindowSpecification (win32) o None. Los diálogos flotantes de Gupta son
    win32 con botones que llevan '&' (aceleradores)."""
    rx = re.compile(patron, re.IGNORECASE)
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            for w in win.windows():
                try:
                    if rx.search(w.window_text() or ""):
                        return win.window(handle=w.handle)
                except Exception:
                    pass
        except Exception:
            pass
        time.sleep(0.5)
    return None


def tipear_en_campo(campo, valor):
    """Llena un Edit con TECLAS REALES. Gupta NO registra set_edit_text para su lógica
    interna (mismo problema que el campo 'cuenta'): hay que teclear de verdad."""
    campo.click_input()                              # foco real en el campo
    time.sleep(0.2)
    campo.type_keys("^a", set_foreground=True)       # seleccionar todo
    campo.type_keys("{BACKSPACE}", set_foreground=True)   # borrar lo que hubiera
    time.sleep(0.1)
    campo.type_keys(valor, with_spaces=True, set_foreground=True)
    campo.type_keys("{END}", set_foreground=True)   # cursor al final -> deselecciona (sin el resaltado azul)
    time.sleep(0.1)


def click_boton(dlg, titulo, nombre_dlg):
    """Enfoca el diálogo y clickea (clic real) un botón por título. Si falla, vuelca los
    controles del diálogo para ver el título/clase reales. Devuelve True/False."""
    try:
        dlg.set_focus()
    except Exception:
        pass
    try:
        dlg.child_window(title=titulo, class_name="Button").click_input()
        return True
    except Exception as e:
        print("  ERROR clickeando '%s' en '%s': %s" % (titulo, nombre_dlg, e))
        print("  Controles de '%s':" % nombre_dlg)
        try:
            dlg.print_control_identifiers(depth=2)
        except Exception:
            pass
        return False


def _dump_menu(frame):
    """Diagnóstico: imprime el menú principal de BAS (items y submenús) para saber los
    nombres reales si menu_select falla."""
    try:
        mnu = frame.menu()
    except Exception as e:
        print("   No pude leer el menú:", e); return
    if mnu is None:
        print("   (la ventana no expone un menú estándar de Windows)"); return
    try:
        for it in mnu.items():
            try:
                txt = it.text()
                print("   • '%s'" % txt)
                sub = it.sub_menu()
                if sub:
                    for s in sub.items():
                        try:
                            stxt = s.text()
                            print("       - '%s'" % stxt)
                            sub2 = s.sub_menu()
                            if sub2:
                                for s2 in sub2.items():
                                    try: print("           · '%s'" % s2.text())
                                    except Exception: pass
                        except Exception: pass
            except Exception: pass
    except Exception as e:
        print("   No pude recorrer el menú:", e)


def abrir_pantalla_conciliacion(frame):
    """Navega el menú de BAS para abrir 'Conciliación bancaria'
    (Procesos > Conciliación Bancaria > Ingreso). Devuelve True/False."""
    rutas = [
        u"Procesos->Conciliación Bancaria->Ingreso",
        u"Procesos->Conciliacion Bancaria->Ingreso",
        u"Procesos->Conciliación bancaria->Ingreso",
    ]
    for r in rutas:
        try:
            frame.menu_select(r)
            return True
        except Exception:
            continue
    return False


def main():
    # 0) Traer BAS AL FRENTE. Es CLAVE: los clics de pywinauto son por coordenadas de pantalla;
    #    si la terminal (u otra ventana) tapa BAS, los clics caen ahí y las teclas se pierden.
    print(">> Empresa=%s  Cuenta banco=%s -> BAS=%s  Archivo=%s" % (EMPRESA or "?", CUENTA_BANCO, CUENTA, ARCHIVO))
    print(">> Descripción=%s" % DESCRIPCION)
    try:
        winframe = win.window(title_re=".*BASCS XE.*")
        winframe.wait("exists", timeout=10)
        winframe.set_focus()          # sube BAS al frente
        time.sleep(0.6)
        paso("BAS traído al frente.")
    except Exception as e:
        print("No pude activar la ventana de BAS. ¿Está abierto? ¿Admin?  Detalle:", e); return

    # 1) Asegurar la pantalla "Conciliación bancaria". Si no está abierta, la abrimos desde
    #    el menú (Procesos > Conciliación Bancaria > Ingreso).
    frame = uia.window(title_re=".*BASCS XE.*")
    try:
        frame.wait("exists", timeout=10)
    except Exception as e:
        print("No encontré la ventana de BAS (uia):", e); return
    form = frame.child_window(title_re="Concilia.*bancaria", control_type="Window")
    if not form.exists():
        paso("'Conciliación bancaria' no está abierta. Navegando el menú (Procesos > Conciliación Bancaria > Ingreso)...")
        if not abrir_pantalla_conciliacion(winframe):
            print("  No pude navegar el menú. Menú principal de BAS:")
            _dump_menu(winframe)
            print("  Abrí 'Conciliación bancaria' a mano y volvé a correr, o pasame el menú de arriba.")
            return
        try:
            form.wait("exists", timeout=15)
        except Exception:
            print("  Seleccioné el menú pero no apareció 'Conciliación bancaria' en 15s."); return
    paso("Form 'Conciliación bancaria' listo.")

    # 2) Tipear la cuenta con TECLAS REALES (set_edit_text no dispara la lógica de Gupta) y
    #    confirmar con ENTER para que arranque la búsqueda de movimientos.
    #    Busco el control por auto_id DESDE EL MARCO (estable), no desde la ventana hija.
    try:
        try: winframe.set_focus(); time.sleep(0.2)    # re-asegurar BAS adelante
        except Exception: pass
        cta = frame.child_window(auto_id="4100", control_type="Edit")
        cta.wait("exists", timeout=10)
        cta.click_input()                 # foco real
        time.sleep(0.3)
        cta.type_keys(CUENTA + "{ENTER}", set_foreground=True)   # teclea 011 y confirma
        paso("Cuenta tecleada: " + CUENTA + " + ENTER  (MIRÁ BAS: ¿carga movimientos / habilita Archivo?)")
    except Exception as e:
        print("  ERROR cargando la cuenta:", e); return

    # 3) "Archivo" se HABILITA enseguida pero NO responde hasta que terminan de cargar los
    #    movimientos. Por eso: espero a que esté habilitado y luego clickeo REINTENTANDO hasta
    #    que realmente abra "Conciliación por archivo" (mientras carga, el click se ignora).
    # El botón "Archivo" lo tomo por WIN32 (Button estándar; 'enabled' confiable). Está dentro
    # del marco de BAS.
    winframe = win.window(title_re=".*BASCS XE.*")
    arch = winframe.child_window(title="&Archivo", class_name="Button")
    try:
        arch.wait("enabled", timeout=30)
    except Exception as e:
        print("  El botón 'Archivo' no se habilitó / no se encontró:", e); return
    # UN SOLO clic (mientras carga la UI está congelada y el clic queda encolado; se procesa
    # cuando terminan de venir los movimientos). NO reintentamos clics: sólo esperamos la ventana.
    paso("'Archivo' habilitado. Un clic y espero (sin reclickear) a que abra la pantalla...")
    try: winframe.set_focus(); time.sleep(0.2)     # re-asegurar BAS adelante antes del clic
    except Exception: pass
    try:
        arch.click_input()
    except Exception as e:
        print("  ERROR clickeando 'Archivo':", e); return
    # La ventana "Conciliación por archivo" SÍ abre, pero el título real puede diferir en
    # mayúsculas/acentos -> la busco case-insensitive. Si no aparece, listo las ventanas reales.
    cpa = buscar_dialogo(r"concilia.*archivo", timeout=120)
    if cpa is None:
        print("  No detecté 'Conciliación por archivo' (win32) en 120s. Ventanas abiertas:")
        _listar_ventanas(); return
    paso("'Conciliación por archivo' detectada: '%s'" % cpa.window_text())

    # 4) "Conciliación por archivo" -> &Archivos
    if not click_boton(cpa, "&Archivos", "Conciliación por archivo"):
        return
    paso("Click en '&Archivos' -> abre 'Archivos para conciliar'.")

    # 5) "Archivos para conciliar" -> &Agregar
    apc = buscar_dialogo(r"archivos\s+para\s+concilia", timeout=30)
    if apc is None:
        print("  No detecté 'Archivos para conciliar'. Ventanas abiertas:")
        _listar_ventanas(); return
    if not click_boton(apc, "&Agregar", "Archivos para conciliar"):
        return
    paso("Click en '&Agregar' -> abre 'Importación del archivo de conciliación'.")

    # 6) "Importación del archivo de conciliación" -> llenar Archivo + Descripción
    imp = buscar_dialogo(r"importaci.n.*archivo.*concilia", timeout=30)
    if imp is None:
        print("  No detecté 'Importación del archivo de conciliación'. Ventanas abiertas:")
        _listar_ventanas(); return
    try:
        campo_arch = imp.child_window(best_match="ArchivoEdit")
        campo_arch.wait("exists", timeout=10)
        tipear_en_campo(campo_arch, ARCHIVO)
        paso("Archivo cargado (tecleado): " + ARCHIVO)
    except Exception as e:
        print("  ERROR cargando Archivo:", e)
        try: imp.print_control_identifiers(depth=2)
        except Exception: pass
        return
    ok_desc = False
    for bm in ("DescripciónEdit", "DescripcionEdit", "Edit6"):
        try:
            campo_desc = imp.child_window(best_match=bm)
            campo_desc.wait("exists", timeout=3)
            tipear_en_campo(campo_desc, DESCRIPCION); ok_desc = True
            paso("Descripción cargada (tecleada): " + DESCRIPCION); break
        except Exception:
            continue
    if not ok_desc:
        print("  ERROR: no ubiqué el campo Descripción."); return

    # 7) Importar + Ok. El &Importar CIERRA el form al terminar. Si el clic no "toma"
    #    (Gupta a veces ignora el clic real), reintento por teclado con el acelerador Alt+I.
    if IMPORTAR:
        try:
            btn_imp = imp.child_window(title="&Importar", class_name="Button")
            btn_imp.wait("exists", timeout=10)
        except Exception as e:
            print("  No ubiqué el botón '&Importar':", e)
            try: imp.print_control_identifiers(depth=2)
            except Exception: pass
            return
        try:
            imp.set_focus(); time.sleep(0.3)
            btn_imp.click_input()
        except Exception as e:
            print("  ERROR clickeando '&Importar':", e); return
        paso("Click en '&Importar'. Verificando que haya tomado...")

        # Si a los ~4s el form SIGUE abierto, el clic no tomó -> reintento con Alt+I (acelerador).
        time.sleep(4)
        try:
            sigue = imp.exists()
        except Exception:
            sigue = False
        if sigue:
            paso("El form sigue abierto: reintento con acelerador de teclado (Alt+I).")
            try:
                imp.set_focus(); time.sleep(0.2)
                imp.type_keys("%i", set_foreground=True)   # Alt+I = &Importar
            except Exception as e:
                print("  (el fallback Alt+I falló: %s)" % e)

        # Al terminar, el form de importación se cierra (vuelve a "Archivos para conciliar").
        t0 = time.time(); cerrado = False
        while time.time() - t0 < 90:
            try:
                if not imp.exists():
                    cerrado = True; break
            except Exception:
                cerrado = True; break
            time.sleep(0.3)
        if not cerrado:
            print("  El form de importación no se cerró en 90s (¿quedó un cartel abierto?)."); return
        paso("Importación terminada en %.1fs (form cerrado)." % (time.time() - t0))

        # Ya en "Archivos para conciliar" -> &Ok (vuelve a "Conciliación por archivo").
        # Mismo criterio que Importar: clic real y, si la ventana no cierra, reintento Alt+O.
        apc2 = buscar_dialogo(r"archivos\s+para\s+concilia", timeout=15)
        if apc2 is None:
            print("  No detecté 'Archivos para conciliar' para el &Ok final. Ventanas:")
            _listar_ventanas(); return
        try:
            btn_ok = apc2.child_window(title="&Ok", class_name="Button")
            btn_ok.wait("exists", timeout=10)
        except Exception as e:
            print("  No ubiqué el botón '&Ok':", e)
            try: apc2.print_control_identifiers(depth=2)
            except Exception: pass
            return
        try:
            apc2.set_focus(); time.sleep(0.3)
            btn_ok.click_input()
        except Exception as e:
            print("  ERROR clickeando '&Ok':", e); return
        paso("Click en '&Ok'. Verificando que la ventana cierre...")

        # Si a los ~3s "Archivos para conciliar" sigue abierta, el clic no tomó -> Alt+O.
        time.sleep(3)
        try:
            sigue_ok = apc2.exists()
        except Exception:
            sigue_ok = False
        if sigue_ok:
            paso("'Archivos para conciliar' sigue abierta: reintento con Alt+O.")
            try:
                apc2.set_focus(); time.sleep(0.2)
                apc2.type_keys("%o", set_foreground=True)   # Alt+O = &Ok
            except Exception as e:
                print("  (el fallback Alt+O falló: %s)" % e)

        # Verificar que efectivamente cerró (volvió a "Conciliación por archivo").
        t0 = time.time(); cerrado_ok = False
        while time.time() - t0 < 30:
            try:
                if not apc2.exists():
                    cerrado_ok = True; break
            except Exception:
                cerrado_ok = True; break
            time.sleep(0.3)
        if cerrado_ok:
            paso("'&Ok' tomado -> volvió a 'Conciliación por archivo'. LISTO.")
        else:
            print("  OJO: 'Archivos para conciliar' no cerró tras el &Ok (revisá manualmente).")
    else:
        paso("MODO PRUEBA (IMPORTAR=False): campos llenados, NO se importó. Verificá y poné IMPORTAR=True.")
    return True


def _cartel_final(exito):
    """Cartel modal al terminar. TOPMOST -> sale DELANTE de BAS (la macro corre elevada, igual
    que BAS). Ok si terminó bien; error apuntando al log si algo falló."""
    import ctypes
    if exito:
        texto, icono = "Conciliación importada en BAS correctamente.", 0x40           # MB_ICONINFORMATION
    else:
        texto, icono = ("No se pudo completar la conciliación.\n"
                        "Revisá el detalle en conciliar.log."), 0x10                   # MB_ICONERROR
    try:
        # MB_TOPMOST (0x40000) + MB_SETFOREGROUND (0x10000) -> queda al frente, delante de BAS.
        ctypes.windll.user32.MessageBoxW(0, texto, "Conciliación BAS", icono | 0x40000 | 0x10000)
    except Exception:
        pass


def _volver_a(hwnd):
    """Restaura al frente la ventana `hwnd` (el portal/navegador desde donde se disparó).
    La macro corre elevada y fue la que cambió el foco, así que puede devolverlo."""
    if not hwnd:
        return
    import ctypes
    try:
        u = ctypes.windll.user32
        if u.IsIconic(hwnd):           # SOLO si está minimizada la restauramos
            u.ShowWindow(hwnd, 9)      # SW_RESTORE  (no tocar si está maximizada/normal)
        u.SetForegroundWindow(hwnd)
    except Exception:
        pass


if __name__ == "__main__":
    # Capturamos la ventana en foco AL INICIO (el lanzador es oculto -> sigue siendo el
    # portal/navegador) para volver ahí al terminar.
    try:
        import ctypes
        _hwnd_portal = ctypes.windll.user32.GetForegroundWindow()
    except Exception:
        _hwnd_portal = 0
    exito = False
    try:
        exito = bool(main())
    except Exception:
        import traceback; traceback.print_exc()
    _cartel_final(exito)
    _volver_a(_hwnd_portal)            # volver al portal
