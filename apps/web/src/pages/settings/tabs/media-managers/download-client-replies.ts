import type { RecordedReply } from "../../../../test/recorded-weir";

/**
 * Replies a running Weir server gave to the requests Setup › Connections › Download clients makes, kept word for word.
 * The three connections were added in this order: SABnzbd, qBittorrent, Deluge.
 */

/** The three connections, as the list shows them. */
export const connectionList: RecordedReply = {
  status: 200,
  body: '[{"id":1,"kind":"sabnzbd","name":"SABnzbd on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:18080","username":null,"password_is_saved":false,"api_key_is_saved":true,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null},{"id":2,"kind":"qbittorrent","name":"qBittorrent on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:18081","username":"admin","password_is_saved":true,"api_key_is_saved":false,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null},{"id":3,"kind":"deluge","name":"Deluge on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:18082","username":null,"password_is_saved":true,"api_key_is_saved":false,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null}]',
};

/** The answer to saving the SABnzbd connection with a new API key. */
export const sabnzbdKeyReplaced: RecordedReply = {
  status: 200,
  body: '{"id":1,"kind":"sabnzbd","name":"SABnzbd on localhost","nickname":null,"enabled":true,"base_url":"http://localhost:18080","username":null,"password_is_saved":false,"api_key_is_saved":true,"last_test_ok":null,"last_test_at":null,"last_test_detail":null,"last_answer_ms":null,"last_used_at":null}',
};
