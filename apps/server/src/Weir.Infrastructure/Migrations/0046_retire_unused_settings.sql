-- Weir schema 0046 (revision 0081_retire_unused_settings): three groups of settings that nothing acted on are gone.
--
-- A connection kept two automatic search lanes ("missing" and "upgrade"), each with its own schedule, but nothing ever ran a
-- search from one: searching belongs to the media manager. Suite settings kept a note for the signed-in home page that no page
-- showed. Performance kept an hours window for movies and another for TV that admission never read, because each workflow's
-- own weekly schedule decides when it works. An older client or a backup that still sends any of them is answered as though it
-- had not (see RetiredSettings in the API); nothing a person saved here changed what Weir did.
DROP TABLE media_manager_search_lanes;

ALTER TABLE suite_settings DROP COLUMN signed_in_home_notice;

ALTER TABLE operator_settings DROP COLUMN movie_schedule_hours_limited;
ALTER TABLE operator_settings DROP COLUMN movie_schedule_days;
ALTER TABLE operator_settings DROP COLUMN movie_schedule_start;
ALTER TABLE operator_settings DROP COLUMN movie_schedule_end;
ALTER TABLE operator_settings DROP COLUMN tv_schedule_hours_limited;
ALTER TABLE operator_settings DROP COLUMN tv_schedule_days;
ALTER TABLE operator_settings DROP COLUMN tv_schedule_start;
ALTER TABLE operator_settings DROP COLUMN tv_schedule_end;
