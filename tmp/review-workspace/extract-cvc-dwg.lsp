(vl-load-com)
(defun dv-sanitize (s)
  (if s
    (progn
      (setq s (vl-string-subst " " "\n" s))
      (setq s (vl-string-subst " " "\r" s))
      s)
    ""))
(defun dv-write-object (obj f / name layer text measure minpt maxpt err)
  (setq name (vla-get-ObjectName obj))
  (setq layer (vla-get-Layer obj))
  (setq text "")
  (if (vlax-property-available-p obj 'TextString)
    (setq text (dv-sanitize (vla-get-TextString obj))))
  (setq measure "")
  (if (vlax-property-available-p obj 'Measurement)
    (setq measure (rtos (vla-get-Measurement obj) 2 6)))
  (setq minpt nil maxpt nil)
  (setq err (vl-catch-all-apply 'vla-GetBoundingBox (list obj 'minpt 'maxpt)))
  (if (not (vl-catch-all-error-p err))
    (progn
      (setq minpt (vlax-safearray->list minpt))
      (setq maxpt (vlax-safearray->list maxpt))))
  (write-line
    (strcat name "|" layer "|" text "|" measure "|"
      (if minpt (vl-princ-to-string minpt) "") "|"
      (if maxpt (vl-princ-to-string maxpt) "")) f)
  (if (= name "AcDbBlockReference")
    (if (= :vlax-true (vla-get-HasAttributes obj))
      (foreach att (vlax-invoke obj 'GetAttributes)
        (write-line (strcat "ATTRIB|" layer "|" (dv-sanitize (vla-get-TagString att)) "=" (dv-sanitize (vla-get-TextString att)) "|||" ) f)))))
(defun c:DVEXTRACT (/ doc blocks block f obj)
  (setq doc (vla-get-ActiveDocument (vlax-get-acad-object)))
  (setq blocks (vla-get-Blocks doc))
  (setq f (open "C:/Users/ADMIN/AppData/Local/Temp/cvc-dwg-extract.txt" "w"))
  (vlax-for block blocks
    (if (= :vlax-true (vla-get-IsLayout block))
      (progn
        (write-line (strcat "LAYOUT|" (vla-get-Name block)) f)
        (vlax-for obj block (dv-write-object obj f)))))
  (close f)
  (princ "\nDVEXTRACT_DONE\n")
  (princ))
