/* ============================================================
   CORRECCIONES PENDIENTES — requieren cuenta con privilegios.
   usr_migracion_adjuntos no tiene UPDATE en GD_Archivo ni ALTER
   en sp_mig_pendientes_orden. Revisar antes de ejecutar.
   ============================================================ */

/* ------------------------------------------------------------
   1) HP_SPRING_PRD — corregir RutaArchivo de archivos cuyo nombre
      vino en Unicode NFD (acento descompuesto): SQL Server guardo
      '´' suelto (0xB4) y la ruta no apuntaba al archivo fisico.
      Los archivos ya fueron renombrados a NFC en el share; este
      UPDATE alinea la BD. Sin el UPDATE los 3 documentos no abren.
   ------------------------------------------------------------ */
USE HP_SPRING_PRD;
GO

UPDATE dbo.GD_Archivo
SET RutaArchivo = '\\172.16.20.24\SpringGestionDoc\PROD\CORP AGRICOLA OLMOS\Expediente de Compras\Orden Compra\2026\09-Setiembre\0000001842\GUÍA DE REMISIÓN EG07-557.pdf'
WHERE ArchivoId IN (163316, 163317);

UPDATE dbo.GD_Archivo
SET RutaArchivo = '\\172.16.20.24\SpringGestionDoc\PROD\BERRY HARVEST\Expediente Compras\Orden Servicio\2026\09-Setiembre\6-00007547\20600876491-09-T096-27 DEV. DE DESCARTE DE ARÁNDANO HASS PERU.pdf'
WHERE ArchivoId = 164705;
GO

/* ------------------------------------------------------------
   1b) HP_SPRING_PRD — el mismo error 500 de run-orden: filas de
       AP_Documentos con obligacion NULL (ej. cabecera SO 6-00006820)
       hacen que FOR JSON omita 't'/'n' y la API explote con
       KeyNotFoundException. Filtrarlas: no pueden mapear a ninguna
       obligacion del portal.
       Ejecutar tambien en HASS_SPRING_PRUEBA si el modo PRUEBA se
       reactiva.
   ------------------------------------------------------------ */
CREATE OR ALTER PROCEDURE dbo.sp_mig_obligaciones_referencia
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @numero VARCHAR(30) = JSON_VALUE(@json, '$.numero');
        DECLARE @tipo VARCHAR(5) = NULLIF(JSON_VALUE(@json, '$.tipo'), '');

        SET @__data = ISNULL((
            SELECT DISTINCT CAST(d.Proveedor AS VARCHAR(20)) AS p,
                   RTRIM(d.ObligacionTipoDocumento) AS t,
                   RTRIM(d.ObligacionNumeroDocumento) AS n
            FROM dbo.AP_Documentos d
            WHERE RTRIM(d.ReferenciaNumeroDocumento) = @numero
              AND (@tipo IS NULL OR RTRIM(d.ReferenciaTipoDocumento) = @tipo)
              AND d.Proveedor IS NOT NULL
              AND d.ObligacionTipoDocumento IS NOT NULL
              AND d.ObligacionNumeroDocumento IS NOT NULL
            FOR JSON PATH), '[]');
        EXEC dbo.sp_mig_respuesta 'success', 'Obligaciones de referencia', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO

/* ------------------------------------------------------------
   2) MS_HASS_PROVEEDOR — incluir SIN_CONTENIDO en el conteo de
      pendientes por orden, para que el boton "Procesar orden"
      permita reprocesar un adjunto cuyo blob llego vacio y luego
      fue corregido en el portal. El script del servicio ya lo
      reprocesa (filtro != 'COMPLETADO'); sin este cambio la API
      responde "no tiene archivos pendientes" y no lanza la corrida.
   ------------------------------------------------------------ */
USE MS_HASS_PROVEEDOR;
GO

CREATE OR ALTER PROCEDURE dbo.sp_mig_pendientes_orden
    @json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @__data NVARCHAR(MAX);
    DECLARE @err NVARCHAR(500);
    BEGIN TRY
        DECLARE @numero NVARCHAR(50) = JSON_VALUE(@json, '$.numero');
        DECLARE @tipo VARCHAR(5) = NULLIF(JSON_VALUE(@json, '$.tipo'), '');

        DECLARE @obls TABLE (p VARCHAR(20), t VARCHAR(5), n VARCHAR(30),
                             id_obl NVARCHAR(400));
        INSERT INTO @obls (p, t, n, id_obl)
        SELECT p, t, n, p + '|' + t + '|' + n
        FROM OPENJSON(@json, '$.obligaciones')
        WITH (p VARCHAR(20) '$.p', t VARCHAR(5) '$.t', n VARCHAR(30) '$.n');

        DECLARE @ap INT = (
            SELECT COUNT(*)
            FROM dbo.MS_AP_DocumentoArchivo a
            LEFT JOIN dbo.MS_AP_DocumentoArchivoTraslado t ON t.IdApDocumentoArchivo = a.IdApDocumentoArchivo
            WHERE a.Gadatos IS NOT NULL
              AND (t.IdApDocumentoArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR', 'OMITIDO', 'SIN_AP', 'SIN_CONTENIDO'))
              AND EXISTS (SELECT 1 FROM @obls o
                          WHERE CAST(a.Proveedor AS VARCHAR(20)) = o.p
                            AND RTRIM(a.ObligacionTipoDocumento) = o.t
                            AND RTRIM(a.ObligacionNumeroDocumento) = o.n));

        DECLARE @ms INT = (
            SELECT COUNT(*)
            FROM dbo.MS_Archivo a
            LEFT JOIN dbo.MS_ArchivoTraslado t ON t.IdArchivo = a.IdArchivo
            WHERE a.Contenido IS NOT NULL
              AND (t.IdArchivo IS NULL OR t.Estado IN ('PENDIENTE', 'ERROR', 'OMITIDO', 'SIN_AP', 'SIN_CONTENIDO'))
              AND (
                   ((@tipo IS NULL OR @tipo = 'OC') AND RTRIM(a.Tabla) = 'WH_OrdenCompra' AND RTRIM(a.Id) = @numero)
                OR ((@tipo IS NULL OR @tipo = 'SO') AND RTRIM(a.Tabla) = 'Compromiso' AND RTRIM(a.Id) = @numero)
                OR (RTRIM(a.Tabla) = 'Obligaciones' AND RTRIM(a.Id) IN (SELECT id_obl FROM @obls))
              ));

        SET @__data = (SELECT @ap AS ap, @ms AS ms, @ap + @ms AS total
                       FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        EXEC dbo.sp_mig_respuesta 'success', 'Pendientes de orden', @__data;
    END TRY
    BEGIN CATCH
        SET @err = ERROR_MESSAGE();
        EXEC dbo.sp_mig_respuesta 'error', @err, NULL;
    END CATCH
END;
GO
