/* ============================================================
   Limpieza de duplicados en GD_Archivo (ejecutar en la BD de
   gestion documental: HASS_SPRING_PRUEBA / HP_SPRING_PRD).

   Causa: inserciones repetidas por ejecuciones traslapadas del
   servicio (dedup check-then-insert no atomico). Ya corregido
   con UPDLOCK+HOLDLOCK y sp_getapplock en el servicio.

   PASO 1: revisar lo que se va a inactivar (solo lectura).
   PASO 2: ejecutar la limpieza.
   ============================================================ */

-- PASO 1: preview de duplicados (mismo IdGrupo + RutaArchivo).
-- Conserva el ArchivoId menor (el primero registrado).
SELECT ArchivoId, IdGrupo, RutaArchivo, UltimaFechaModif, Estado
FROM dbo.GD_Archivo a
WHERE EXISTS (
    SELECT 1 FROM dbo.GD_Archivo b
    WHERE b.IdGrupo = a.IdGrupo AND b.RutaArchivo = a.RutaArchivo
      AND b.ArchivoId < a.ArchivoId)
ORDER BY a.RutaArchivo, a.ArchivoId;
GO

-- PASO 2: marcar duplicados como inactivos (soft delete).
-- Preferido sobre DELETE: conserva trazabilidad y no rompe el
-- ArchivoId guardado en MS_*Traslado del portal.
BEGIN TRAN;
UPDATE a SET Estado = 'I', UltimoUsuario = 'MIGRACION', UltimaFechaModif = GETDATE()
FROM dbo.GD_Archivo a
WHERE EXISTS (
    SELECT 1 FROM dbo.GD_Archivo b
    WHERE b.IdGrupo = a.IdGrupo AND b.RutaArchivo = a.RutaArchivo
      AND b.ArchivoId < a.ArchivoId);
-- Revisar el conteo y luego COMMIT (o ROLLBACK para revertir).
SELECT @@ROWCOUNT AS FilasInactivadas;
-- COMMIT TRAN;
GO

-- Alternativa: borrado fisico (usar solo si se prefiere eliminar).
-- DELETE a FROM dbo.GD_Archivo a
-- WHERE EXISTS (
--     SELECT 1 FROM dbo.GD_Archivo b
--     WHERE b.IdGrupo = a.IdGrupo AND b.RutaArchivo = a.RutaArchivo
--       AND b.ArchivoId < a.ArchivoId);
GO

/* Opcional, tras limpiar: guardia dura contra futuros duplicados.
   Verificar primero que no queden duplicados activos. */
-- CREATE UNIQUE INDEX UX_GD_Archivo_Grupo_Ruta
--     ON dbo.GD_Archivo (IdGrupo, RutaArchivo)
--     WHERE Estado = 'A';
GO
